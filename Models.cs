using System.Text.Json.Serialization;

namespace ClipMaster;

public class ClipEntry
{
    [JsonPropertyName("id")]           public string       Id           { get; set; } = "";
    [JsonPropertyName("raw")]          public string       Raw          { get; set; } = "";
    [JsonPropertyName("text")]         public string       Text         { get; set; } = "";
    [JsonPropertyName("copiedAt")]     public long         CopiedAt     { get; set; }
    [JsonPropertyName("copyCount")]    public int          CopyCount    { get; set; } = 1;
    [JsonPropertyName("pinned")]       public bool         Pinned       { get; set; }
    [JsonPropertyName("tags")]         public List<string> Tags         { get; set; } = [];
    [JsonPropertyName("isSensitive")]  public bool         IsSensitive  { get; set; }
    [JsonPropertyName("appliedRules")] public List<string> AppliedRules { get; set; } = [];
    [JsonPropertyName("isImage")]      public bool         IsImage      { get; set; }
    [JsonPropertyName("imagePath")]    public string?      ImagePath    { get; set; }
    [JsonPropertyName("imageWidth")]   public int          ImageWidth   { get; set; }
    [JsonPropertyName("imageHeight")]  public int          ImageHeight  { get; set; }
    [JsonPropertyName("imageBytes")]   public long         ImageBytes   { get; set; }
}

public class Rule
{
    [JsonPropertyName("id")]          public string Id          { get; set; } = "";
    [JsonPropertyName("name")]        public string Name        { get; set; } = "";
    [JsonPropertyName("pattern")]     public string Pattern     { get; set; } = "";
    [JsonPropertyName("flags")]       public string Flags       { get; set; } = "g";
    [JsonPropertyName("replacement")] public string Replacement { get; set; } = "";
    [JsonPropertyName("mode")]        public string Mode        { get; set; } = "auto";
    [JsonPropertyName("enabled")]     public bool   Enabled     { get; set; } = true;
}

public class AppSettings
{
    [JsonPropertyName("maxHistory")]      public int    MaxHistory      { get; set; } = 500;
    [JsonPropertyName("hotkey")]          public string Hotkey          { get; set; } = "Ctrl+`";
    [JsonPropertyName("maskPasswords")]   public bool   MaskPasswords   { get; set; } = true;
    [JsonPropertyName("autoApplyRules")]  public bool   AutoApplyRules  { get; set; } = true;
    [JsonPropertyName("windowWidth")]     public int    WindowWidth     { get; set; } = 480;
    [JsonPropertyName("windowHeight")]    public int    WindowHeight    { get; set; } = 640;
    [JsonPropertyName("runOnStartup")]    public bool   RunOnStartup    { get; set; }
    [JsonPropertyName("maxImageHistory")] public int    MaxImageHistory { get; set; } = 50;
}

public class AppData
{
    [JsonPropertyName("clips")]    public List<ClipEntry> Clips    { get; set; } = [];
    [JsonPropertyName("rules")]    public List<Rule>      Rules    { get; set; } = [];
    [JsonPropertyName("tags")]     public List<string>    Tags     { get; set; } = [];
    [JsonPropertyName("settings")] public AppSettings     Settings { get; set; } = new();
}
