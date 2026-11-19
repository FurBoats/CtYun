using CtYun.Models;
using System.Text.Json.Serialization;

namespace CtYun
{
    // AOT 编译需要
    [JsonSerializable(typeof(ConnecMessage))]
    [JsonSerializable(typeof(TeleagentConfig))]
    internal partial class AppJsonSerializerContext : JsonSerializerContext
    {
    }
}
