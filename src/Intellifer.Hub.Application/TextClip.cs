namespace Intellifer.Hub.Application;

public static class TextClip
{
    public static string Clip(string value, int max) =>
        value.Length <= max ? value : value[..max];

    public static string? ClipOrNull(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return Clip(value, max);
    }
}
