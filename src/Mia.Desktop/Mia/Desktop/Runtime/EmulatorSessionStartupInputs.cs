// SPDX-License-Identifier: MIT

namespace Mia.Desktop.Runtime;

internal sealed record EmulatorSessionStartupInputs(
    byte[] Firmware,
    byte[] Gdfs,
    byte[] Modem,
    byte[] Otp,
    GdfsOverlayStore GdfsOverlayStore,
    GdfsOverlayLoad GdfsLoad,
    string PersistenceKey,
    MiaPersistenceSnapshot PersistenceSnapshot);
