using System.Text.Json;

namespace TaskOrganizer.Web.Integrations;

/// <summary>
/// Reads a JSON answer from an outside service without assuming its shape. A missing or
/// wrongly typed required part means the address is wrong or the service changed, so it is
/// reported as an ExternalServiceException with a readable message, never as a crash.
/// </summary>
public readonly struct JsonAnswer
{
    private readonly JsonElement _element;
    private readonly string _service;
    private readonly string _settingName;

    private JsonAnswer(JsonElement element, string service, string settingName)
    {
        _element = element;
        _service = service;
        _settingName = settingName;
    }

    /// <summary>The whole answer, which must be a JSON object.</summary>
    public static JsonAnswer Root(JsonDocument document, string service, string settingName)
    {
        JsonAnswer root = new(document.RootElement, service, settingName);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw root.Unreadable();
        }

        return root;
    }

    public JsonAnswer RequiredObject(string name)
    {
        return TryGet(name, JsonValueKind.Object, out JsonElement value) ? new JsonAnswer(value, _service, _settingName) : throw Unreadable();
    }

    public string RequiredString(string name)
    {
        return TryGet(name, JsonValueKind.String, out JsonElement value) ? value.GetString() ?? string.Empty : throw Unreadable();
    }

    /// <summary>The object, or null when it is absent or not an object.</summary>
    public JsonAnswer? OptionalObject(string name)
    {
        return TryGet(name, JsonValueKind.Object, out JsonElement value) ? new JsonAnswer(value, _service, _settingName) : null;
    }

    /// <summary>The text, or null when it is absent, null, or not text.</summary>
    public string? OptionalString(string name)
    {
        return TryGet(name, JsonValueKind.String, out JsonElement value) ? value.GetString() : null;
    }

    private bool TryGet(string name, JsonValueKind kind, out JsonElement value)
    {
        if (_element.TryGetProperty(name, out JsonElement found) && found.ValueKind == kind)
        {
            value = found;
            return true;
        }

        value = default;
        return false;
    }

    private ExternalServiceException Unreadable()
    {
        return new ExternalServiceException($"{_service} sent an answer that could not be read. Check the {_settingName} in Settings.");
    }
}
