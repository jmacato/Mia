// SPDX-License-Identifier: MIT

namespace Mia.Emulator.Gsm;

internal enum GsmCompatibilityCellDedicatedDownlinkFrameKind
{
    Ua,
    LocationUpdatingAccept,
    CipheringModeCommand,
    ReceiveReady,
    SmsCpAck,
    SmsRpAck,
    SmsRpError,
    MmInformation,
    PagingSapi3Sabm,
    Segment,
    MobileTerminatedSmsCpData,
    MobileTerminatedCallSetup,
    MobileTerminatedTrafficAssignment,
    MobileTerminatedCallConnectAcknowledge,
    MobileTerminatedCallRelease,
    CallProceeding,
    CallAlerting,
    CallConnect,
    CallReleaseComplete,
    ChannelRelease,
}
