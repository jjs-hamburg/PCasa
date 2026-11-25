using System.Text.Json;

namespace PCasa.App;

public partial class MainWindow
{
    internal static (HashSet<long> Ids, HashSet<string> Paths) ShutdownRecipeTargets(string payload)
    {
        var ids = new HashSet<long>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var json = JsonDocument.Parse(payload);
            Visit(json.RootElement);
        }
        catch (JsonException) { }
        return (ids, paths);

        void Visit(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in node.EnumerateArray()) Visit(element);
                return;
            }
            if (node.ValueKind != JsonValueKind.Object) return;
            foreach (var property in node.EnumerateObject())
            {
                if ((property.Name is "ImageId" or "Id") &&
                    property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt64(out var id) && id > 0)
                    ids.Add(id);
                else if (property.Name == "ImageIds" &&
                    property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var value in property.Value.EnumerateArray())
                        if (value.ValueKind == JsonValueKind.Number &&
                            value.TryGetInt64(out var imageId) && imageId > 0)
                            ids.Add(imageId);
                }
                else if (property.Name == "Path" &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() is { Length: > 0 } path)
                    paths.Add(path);
                else if (property.Name == "Sources" &&
                    property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var value in property.Value.EnumerateArray())
                        if (value.ValueKind == JsonValueKind.String &&
                            value.GetString() is { Length: > 0 } source)
                            paths.Add(source);
                }
                else if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    Visit(property.Value);
            }
        }
    }

}
