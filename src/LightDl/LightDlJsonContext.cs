using System.Text.Json.Serialization;

namespace LightDl;

[JsonSerializable(typeof(LightDownloader.DownloadMetadata))]
[JsonSerializable(typeof(LightDownloader.CompletedRange))]
internal sealed partial class LightDlJsonContext : JsonSerializerContext;
