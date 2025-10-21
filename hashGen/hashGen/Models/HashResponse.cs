namespace hashGen.Models;

public class HashResponse
{
    public string Model { get; set; } = string.Empty;
    public int Count { get; set; }
    public List<string> Hashtags { get; set; } = new();
}