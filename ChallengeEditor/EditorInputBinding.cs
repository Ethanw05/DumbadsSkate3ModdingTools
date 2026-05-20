using System.Text.Json;
using System.Text.Json.Serialization;
using Veldrid;
using Key = Veldrid.Key;

namespace ChallengeEditor;

/// <summary>Single editor hotkey: keyboard <see cref="Veldrid.Key"/> or <see cref="Veldrid.MouseButton"/>.</summary>
[JsonConverter(typeof(EditorInputBindingJsonConverter))]
public readonly struct EditorInputBinding : IEquatable<EditorInputBinding>
{
    public bool IsKey => _tag == 1;
    public bool IsMouse => _tag == 2;

    private readonly byte _tag; // 0 none, 1 key, 2 mouse
    private readonly Key _key;
    private readonly MouseButton _mouse;

    private EditorInputBinding(byte tag, Key key, MouseButton mouse)
    {
        _tag = tag;
        _key = key;
        _mouse = mouse;
    }

    public static EditorInputBinding FromKey(Key key) => new(1, key, default);

    public static EditorInputBinding FromMouse(MouseButton mouse) => new(2, default, mouse);

    public Key Key => IsKey ? _key : Key.Unknown;

    public MouseButton Mouse => IsMouse ? _mouse : default;

    public string DisplayLabel() => _tag switch
    {
        1 => _key.ToString(),
        2 => "Mouse · " + _mouse,
        _ => "(none)"
    };

    public bool Equals(EditorInputBinding other) => _tag == other._tag && _key == other._key && _mouse == other._mouse;

    public override bool Equals(object? obj) => obj is EditorInputBinding o && Equals(o);

    public override int GetHashCode() => HashCode.Combine(_tag, _key, _mouse);

    public static bool operator ==(EditorInputBinding a, EditorInputBinding b) => a.Equals(b);

    public static bool operator !=(EditorInputBinding a, EditorInputBinding b) => !a.Equals(b);
}

/// <summary>
/// JSON: legacy per-field integer = Key. New forms: string <c>m:Right</c> / <c>m:Middle</c> / <c>m:Left</c>,
/// or object <c>{"mouse":"Right"}</c> / <c>{"key":"F2"}</c> (key name optional).
/// </summary>
public sealed class EditorInputBindingJsonConverter : JsonConverter<EditorInputBinding>
{
    public override EditorInputBinding Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
            {
                int v = reader.GetInt32();
                return EditorInputBinding.FromKey((Key)v);
            }
            case JsonTokenType.String:
            {
                string? s = reader.GetString();
                if (string.IsNullOrEmpty(s)) return default;
                if (s.StartsWith("m:", StringComparison.OrdinalIgnoreCase))
                {
                    string name = s[2..].Trim();
                    if (Enum.TryParse<MouseButton>(name, ignoreCase: true, out var mb))
                        return EditorInputBinding.FromMouse(mb);
                    return default;
                }
                if (Enum.TryParse<Key>(s, ignoreCase: true, out var k))
                    return EditorInputBinding.FromKey(k);
                return default;
            }
            case JsonTokenType.StartObject:
            {
                Key? key = null;
                MouseButton? mouse = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    string? prop = reader.GetString();
                    reader.Read();
                    if (string.Equals(prop, "mouse", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.String)
                    {
                        string? ms = reader.GetString();
                        if (Enum.TryParse<MouseButton>(ms, ignoreCase: true, out var mb))
                            mouse = mb;
                    }
                    else if (string.Equals(prop, "key", StringComparison.OrdinalIgnoreCase))
                    {
                        if (reader.TokenType == JsonTokenType.Number)
                            key = (Key)reader.GetInt32();
                        else if (reader.TokenType == JsonTokenType.String)
                        {
                            string? ks = reader.GetString();
                            if (Enum.TryParse<Key>(ks, ignoreCase: true, out var kk))
                                key = kk;
                        }
                    }
                }
                if (mouse.HasValue) return EditorInputBinding.FromMouse(mouse.Value);
                if (key.HasValue) return EditorInputBinding.FromKey(key.Value);
                return default;
            }
            default:
                return default;
        }
    }

    public override void Write(Utf8JsonWriter writer, EditorInputBinding value, JsonSerializerOptions options)
    {
        if (value.IsMouse)
        {
            writer.WriteStringValue("m:" + value.Mouse.ToString());
            return;
        }
        if (value.IsKey)
        {
            writer.WriteNumberValue((int)value.Key);
            return;
        }
        writer.WriteNullValue();
    }
}
