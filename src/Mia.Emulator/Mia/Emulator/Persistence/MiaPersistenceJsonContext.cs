// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace Mia.Emulator.Persistence;

[JsonSerializable(typeof(MiaPersistenceSnapshot))]
internal sealed partial class MiaPersistenceJsonContext : JsonSerializerContext;
