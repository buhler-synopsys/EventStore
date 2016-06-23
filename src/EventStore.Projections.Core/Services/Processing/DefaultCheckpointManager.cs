using System;
using System.Collections.Generic;
using System.Security.Principal;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Helpers;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.UserManagement;
using EventStore.Projections.Core.Messages;

namespace EventStore.Projections.Core.Services.Processing
{
    public class DefaultCheckpointManager : CoreProjectionCheckpointManager,
        IHandle<CoreProjectionCheckpointWriterMessage.CheckpointWritten>,
        IHandle<CoreProjectionCheckpointWriterMessage.RestartRequested>
    {
        private readonly IPrincipal _runAs;
        private readonly CheckpointTag _zeroTag;
        private int _readRequestsInProgress;
        private readonly HashSet<Guid> _loadStateRequests = new HashSet<Guid>();

        protected readonly ProjectionVersion _projectionVersion;
        protected readonly IODispatcher _ioDispatcher;
        private readonly PositionTagger _positionTagger;
        private readonly CoreProjectionCheckpointWriter _coreProjectionCheckpointWriter;
        private PartitionStateUpdateManager _partitionStateUpdateManager;


        public DefaultCheckpointManager(
            IPublisher publisher, Guid projectionCorrelationId, ProjectionVersion projectionVersion, IPrincipal runAs,
            IODispatcher ioDispatcher, ProjectionConfig projectionConfig, string name, PositionTagger positionTagger,
            ProjectionNamesBuilder namingBuilder, bool usePersistentCheckpoints, bool producesRunningResults, bool definesFold,
            CoreProjectionCheckpointWriter coreProjectionCheckpointWriter)
            : base(
                publisher, projectionCorrelationId, projectionConfig, name, positionTagger, namingBuilder,
                usePersistentCheckpoints)
        {
            if (ioDispatcher == null) throw new ArgumentNullException("ioDispatcher");
            _projectionVersion = projectionVersion;
            _runAs = runAs;
            _ioDispatcher = ioDispatcher;
            _positionTagger = positionTagger;
            _coreProjectionCheckpointWriter = coreProjectionCheckpointWriter;
            _zeroTag = positionTagger.MakeZeroCheckpointTag();
        }

        protected override void BeginWriteCheckpoint(
            CheckpointTag requestedCheckpointPosition, string requestedCheckpointState)
        {
            _requestedCheckpointPosition = requestedCheckpointPosition;
            _coreProjectionCheckpointWriter.BeginWriteCheckpoint(
                new SendToThisEnvelope(this), requestedCheckpointPosition, requestedCheckpointState);
        }

        public override void RecordEventOrder(
            ResolvedEvent resolvedEvent, CheckpointTag orderCheckpointTag, Action committed)
        {
            committed();
        }

        public override void PartitionCompleted(string partition)
        {
            _partitionStateUpdateManager.PartitionCompleted(partition);
        }

        public override void Initialize()
        {
            base.Initialize();
            _partitionStateUpdateManager = null;
            foreach (var requestId in _loadStateRequests)
                _ioDispatcher.BackwardReader.Cancel(requestId);
            _loadStateRequests.Clear();
            _coreProjectionCheckpointWriter.Initialize();
            _requestedCheckpointPosition = null;
            _readRequestsInProgress = 0;
        }

        public override void GetStatistics(ProjectionStatistics info)
        {
            base.GetStatistics(info);
            info.ReadsInProgress += _readRequestsInProgress;
            _coreProjectionCheckpointWriter.GetStatistics(info);
        }

        public override void BeginLoadPartitionStateAt(
            string statePartition, CheckpointTag requestedStateCheckpointTag, Action<PartitionState> loadCompleted)
        {
            var stateEventType = ProjectionEventTypes.PartitionCheckpoint;
            var partitionCheckpointStreamName = _namingBuilder.MakePartitionCheckpointStreamName(statePartition);
            _readRequestsInProgress++;
            var requestId = _ioDispatcher.ReadBackward(
                partitionCheckpointStreamName, -1, 1, false, SystemAccount.Principal,
                m =>
                    OnLoadPartitionStateReadStreamEventsBackwardCompleted(
                        m, requestedStateCheckpointTag, loadCompleted, partitionCheckpointStreamName, stateEventType));
            if (requestId != Guid.Empty)
                _loadStateRequests.Add(requestId);
        }

        private void OnLoadPartitionStateReadStreamEventsBackwardCompleted(
            ClientMessage.ReadStreamEventsBackwardCompleted message, CheckpointTag requestedStateCheckpointTag,
            Action<PartitionState> loadCompleted, string partitionStreamName, string stateEventType)
        {
            //NOTE: the following remove may do nothing in tests as completed is raised before we return from publish. 
            _loadStateRequests.Remove(message.CorrelationId);

            _readRequestsInProgress--;
            if (message.Events.Length == 1)
            {
                EventRecord @event = message.Events[0].Event;
                if (@event.EventType == stateEventType)
                {
                    var parsed = @event.Metadata.ParseCheckpointTagVersionExtraJson(_projectionVersion);
                    if (parsed.Version.ProjectionId != _projectionVersion.ProjectionId
                        || _projectionVersion.Epoch > parsed.Version.Version)
                    {
                        var state = new PartitionState("", null, _zeroTag);
                        loadCompleted(state);
                        return;
                    }
                    else
                    {
                        var loadedStateCheckpointTag = parsed.AdjustBy(_positionTagger, _projectionVersion);
                        // always recovery mode? skip until state before current event
                        //TODO: skip event processing in case we know i has been already processed
                        if (loadedStateCheckpointTag < requestedStateCheckpointTag)
                        {
                            var state = PartitionState.Deserialize(
                                Helper.UTF8NoBom.GetString(@event.Data), loadedStateCheckpointTag);
                            loadCompleted(state);
                            return;
                        }
                    }
                }
            }
            if (message.NextEventNumber == -1)
            {
                var state = new PartitionState("", null, _zeroTag);
                loadCompleted(state);
                return;
            }
            _readRequestsInProgress++;
            var requestId = _ioDispatcher.ReadBackward(
                partitionStreamName, message.NextEventNumber, 1, false, SystemAccount.Principal,
                m =>
                    OnLoadPartitionStateReadStreamEventsBackwardCompleted(
                        m, requestedStateCheckpointTag, loadCompleted, partitionStreamName, stateEventType));
            if (requestId != Guid.Empty)
                _loadStateRequests.Add(requestId);
        }

        protected override ProjectionCheckpoint CreateProjectionCheckpoint(CheckpointTag checkpointPosition)
        {
            return new ProjectionCheckpoint(
                _ioDispatcher, _projectionVersion, _runAs, this, checkpointPosition, _positionTagger,
                _projectionConfig.MaxWriteBatchLength, _logger);
        }

        public void Handle(CoreProjectionCheckpointWriterMessage.CheckpointWritten message)
        {
            CheckpointWritten(message.Position);
        }

        public void Handle(CoreProjectionCheckpointWriterMessage.RestartRequested message)
        {
            RequestRestart(message.Reason);
        }

        protected override void CapturePartitionStateUpdated(string partition, PartitionState oldState, PartitionState newState)
        {
            if (_partitionStateUpdateManager == null)
                _partitionStateUpdateManager = new PartitionStateUpdateManager(_namingBuilder);
            _partitionStateUpdateManager.StateUpdated(partition, newState, oldState.CausedBy);
        }

        protected override void EmitPartitionCheckpoints()
        {
            if (_partitionStateUpdateManager != null)
            {
                _partitionStateUpdateManager.EmitEvents(_currentCheckpoint);
                _partitionStateUpdateManager = null;
            }
        }
    }
}
