using System;
using System.Text.Json.Serialization;
using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.Metal;

internal sealed record FramePhaseHeader(string type, string schema, long stopwatchFrequency, int pid,
    DateTime utc, int nativeEventBytes, int nativeClockBytes, string zeroPolicy, bool presentationPolicyChanged);
internal sealed record FramePhaseSubmissionRow(string type, FramePhaseRecorder.Submission item);
internal sealed record FramePhaseClockRow(string type, long before, long after, int status, FramePhaseClock clock);
internal sealed record FramePhasePresentedRow(string type, ulong Sequence, ulong ExpectedDrawableId,
    ulong CallbackDrawableId, double? presentedTime, long presentedTimeBits, string classification,
    ulong AbsoluteBefore, ulong ContinuousBefore, double CaBefore, ulong ContinuousAfter, ulong AbsoluteAfter, double CaAfter);
internal sealed record FramePhaseFooter(string type, bool completed, string writerError, long attempts,
    long registrationErrors, ulong registered, ulong callbacks, ulong pending, ulong mismatches,
    ulong zeroOrInvalidTimes, ulong nativeDrops, ulong rejectedAfterClose, long submissionDrops,
    int queueDepth, long exportedSubmissions, long exportedCallbacks);
internal sealed record FramePhaseLifetimeRow(string type, int destroyStatus, bool retained);

internal sealed record FramePhaseAlignmentRow(string type, bool enabled, long feedbackCount,
    long adjustments, long totalTicks, long maximumAbsTicks);

internal sealed record FramePhaseOpportunityHeader(string type, string schema, bool enabled,
    bool phaseAlignmentEnabled, int capacity, string feedbackSequenceSemantics,
    string writerObservedSemantics, string opportunityPoint, string captureScope);
internal sealed record FramePhaseOpportunityRow(string type, long opportunitySequence, long tickSample,
    long nominalDeadline, long now, long ticksPerFrame, long tickRemainder, int swapInterval,
    int swapIntervalDelay, bool composeCalled, bool phaseEnabled, bool vSyncModeSwitch,
    long targetVSyncInterval, bool correctionCalled, string reason, ulong? feedbackSequence,
    long? mappedPresentedTicks, long? writerObservedTicks, long correctionTicks,
    double? wrappedPhaseTicks, double? signedPhaseErrorTicks);
internal sealed record FramePhaseOpportunityFooter(string type, PhaseOpportunityStatistics statistics,
    bool completed, bool lossFree, long exportedOpportunities, string writerError, string captureScope,
    long? closeBeforeTicks, long? closeAfterTicks, bool captureBracketValid);

internal sealed record PresentBufferHeader(string type, string schema, int abiVersion, int eventBytes, int statsBytes,
    int nativeCapacity, int managedCapacity, string optIn, string identitySemantics, string cardinality,
    string clockSemantics, bool presentationPolicyChanged);
internal sealed record PresentBufferRegistrationRow(string type, ulong instance, ulong sequence, ulong drawableId,
    ulong bufferPointer, int poolIndex, int registerStatus, int presentedRegisterStatus, long beforeTicks, long afterTicks);
internal sealed record PresentBufferCommitRow(string type, ulong instance, ulong sequence, ulong drawableId,
    ulong bufferPointer, int poolIndex, long beforeTicks, long afterTicks, bool returned);
internal sealed record PresentBufferCallbackRow(string type, ulong instance, ulong sequence, ulong drawableId,
    ulong expectedBufferPointer, ulong callbackBufferPointer, uint kind, uint observedStatus, uint gpuValidity,
    uint errorPresent, long errorCode, double? gpuStartTime, long gpuStartTimeBits, double? gpuEndTime, long gpuEndTimeBits,
    ulong absoluteBefore, ulong continuousBefore, double? caBefore, ulong continuousAfter, ulong absoluteAfter,
    double? caAfter, uint numer, uint denom);
internal sealed record PresentBufferFooter(string type, bool completed, bool lossFree, string writerError,
    long attempts, long refusals, long managedDrops, int queueDepth, int commitLeases, long exportedRegistrations,
    long exportedCommits, long exportedScheduled, long exportedCompleted, PresentBufferStatistics statistics,
    long closeBeforeTicks, long closeAfterTicks);
internal sealed record PresentBufferLifetime(string type, int destroyStatus, bool retained);

[JsonSourceGenerationOptions(IncludeFields = true)]
[JsonSerializable(typeof(PresentBufferHeader))]
[JsonSerializable(typeof(PresentBufferRegistrationRow))]
[JsonSerializable(typeof(PresentBufferCommitRow))]
[JsonSerializable(typeof(PresentBufferCallbackRow))]
[JsonSerializable(typeof(PresentBufferFooter))]
[JsonSerializable(typeof(PresentBufferLifetime))]
[JsonSerializable(typeof(FramePhaseHeader))]
[JsonSerializable(typeof(FramePhaseSubmissionRow))]
[JsonSerializable(typeof(FramePhaseClockRow))]
[JsonSerializable(typeof(FramePhasePresentedRow))]
[JsonSerializable(typeof(FramePhaseFooter))]
[JsonSerializable(typeof(FramePhaseLifetimeRow))]
[JsonSerializable(typeof(FramePhaseAlignmentRow))]
[JsonSerializable(typeof(FramePhaseOpportunityHeader))]
[JsonSerializable(typeof(FramePhaseOpportunityRow))]
[JsonSerializable(typeof(FramePhaseOpportunityFooter))]
internal partial class FramePhaseJsonContext : JsonSerializerContext { }
