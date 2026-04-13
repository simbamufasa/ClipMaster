# Clipboard Encryption Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Encrypt `~/.clipmaster/data.bin` at rest using Windows DPAPI so clipboard history is unreadable by other users, other processes, or anyone with physical drive access.

**Architecture:** `DataService` gains a private `Protect`/`Unprotect` pair wrapping `System.Security.Cryptography.ProtectedData`. `Save()` writes encrypted bytes; `Load()` decrypts them. A one-time silent migration promotes `data.json` → `data.bin` on first run. A tray-menu export action writes a portable plain-JSON backup on demand.

**Tech Stack:** .NET 8 WPF, `System.Security.Cryptography.ProtectedData` NuGet package, xUnit 2.x (new test project), `Microsoft.Win32.SaveFileDialog` (already in WPF SDK)

---

## File Map

| File | Change |
|---|---|
| `ClipMaster.csproj` | Add `System.Security.Cryptography.ProtectedData` NuGet ref; add test project ref if needed |
| `ClipMaster.Tests/ClipMaster.Tests.csproj` | **Create** — xUnit test project |
| `ClipMaster.Tests/DataServiceEncryptionTests.cs` | **Create** — tests for encrypt/decrypt, migration, stale-tmp cleanup |
| `DataService.cs` | Inject optional `dataDir`, replace Load/Save with DPAPI path, add `ExportBackup` |
| `App.xaml.cs` | Pass no args to `DataService()` (default); wire `ExportRequested` → `OnExportBackup` |
| `TrayMenu.xaml` | Add "Export backup…" menu item between Show and separator |
| `TrayMenu.xaml.cs` | Add `ExportRequested` event + `ExportItem_Click` handler |

---

## Task 1: Add NuGet package and create test project

**Files:**
- Modify: `ClipMaster.csproj`
- Create: `ClipMaster.Tests/ClipMaster.Tests.csproj`

- [ ] **Step 1: Add DPAPI NuGet to main project**

Edit `ClipMaster.csproj` — add inside the existing `<ItemGroup>` that has `<Resource>` entries, or add a new `<ItemGroup>`:

```xml
<ItemGroup>
  <PackageReference Include="System.Security.Cryptography.ProtectedData" Version="8.0.0" />
</ItemGroup>
```

Full updated file:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ApplicationIcon>Assets\icon.ico</ApplicationIcon>
    <AssemblyName>ClipMaster</AssemblyName>
    <RootNamespace>ClipMaster</RootNamespace>
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
  </PropertyGroup>
  <ItemGroup>
    <Resource Include="Assets\icon.ico" />
    <Resource Include="Assets\icon-32.png" />
    <Resource Include="Themes\Dark.xaml" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="System.Security.Cryptography.ProtectedData" Version="8.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create test project**

Create `ClipMaster.Tests/ClipMaster.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.9.0" />
    <PackageReference Include="xunit" Version="2.7.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.5.7">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="System.Security.Cryptography.ProtectedData" Version="8.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\ClipMaster.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Verify it builds**

```
dotnet build ClipMaster.csproj
dotnet build ClipMaster.Tests/ClipMaster.Tests.csproj
```

Expected: both succeed with no errors.

- [ ] **Step 4: Commit**

```bash
git add ClipMaster.csproj ClipMaster.Tests/ClipMaster.Tests.csproj
git commit -m "build: add DPAPI NuGet package and xUnit test project"
```

---

## Task 2: Make DataService testable — inject data directory

**Files:**
- Modify: `DataService.cs`

Currently `DataService` hardcodes `~/.clipmaster`. Change it to accept an optional override so tests can point at a temp directory without touching the real user profile.

- [ ] **Step 1: Write the failing test**

Create `ClipMaster.Tests/DataServiceEncryptionTests.cs`:

```csharp
using System.IO;
using ClipMaster;
using Xunit;

namespace ClipMaster.Tests;

public class DataServiceEncryptionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public DataServiceEncryptionTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose()              => Directory.Delete(_tempDir, recursive: true);

    private DataService Svc() => new(_tempDir);

    [Fact]
    public void Load_ReturnsEmpty_WhenNeitherFileExists()
    {
        var result = Svc().Load();
        Assert.Empty(result.Clips);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```
dotnet test ClipMaster.Tests/ClipMaster.Tests.csproj --filter "Load_ReturnsEmpty_WhenNeitherFileExists" -v minimal
```

Expected: compile error — `DataService` has no constructor that accepts a string.

- [ ] **Step 3: Add the injectable constructor to DataService**

Replace the four static readonly fields at the top of `DataService` with instance fields and an injectable constructor. The existing zero-arg usage in `App.xaml.cs` (`new DataService()`) continues to work unchanged.

Replace this block in `DataService.cs`:
```csharp
public class DataService
{
    private static readonly string DataDir  = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clipmaster");
    private static readonly string DataFile = Path.Combine(DataDir, "data.json");

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
```

With:
```csharp
public class DataService
{
    private readonly string _dataDir;
    private readonly string _dataFile;   // legacy plaintext path (migration source)
    private readonly string _binFile;    // encrypted binary path
    private readonly string _binTmp;     // in-progress write temp path

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public DataService(string? dataDir = null)
    {
        _dataDir  = dataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clipmaster");
        _dataFile = Path.Combine(_dataDir, "data.json");
        _binFile  = Path.Combine(_dataDir, "data.bin");
        _binTmp   = Path.Combine(_dataDir, "data.bin.tmp");
    }
```

- [ ] **Step 4: Run test — expect pass**

```
dotnet test ClipMaster.Tests/ClipMaster.Tests.csproj --filter "Load_ReturnsEmpty_WhenNeitherFileExists" -v minimal
```

Expected: PASS (Load still returns `new AppData()` when no file exists; constructor change is additive).

- [ ] **Step 5: Commit**

```bash
git add DataService.cs ClipMaster.Tests/DataServiceEncryptionTests.cs
git commit -m "refactor(DataService): inject data directory for testability"
```

---

## Task 3: Encrypt Save, decrypt Load, silent migration

**Files:**
- Modify: `DataService.cs`
- Modify: `ClipMaster.Tests/DataServiceEncryptionTests.cs`

- [ ] **Step 1: Write the failing tests**

Add these test methods to `DataServiceEncryptionTests.cs` (inside the class, after the existing test):

```csharp
[Fact]
public void SaveAndLoad_RoundTripsData()
{
    var svc  = Svc();
    var data = new AppData();
    data.Clips.Add(new ClipEntry { Id = "c1", Raw = "hello world", Text = "hello world" });

    svc.Save(data);

    Assert.True(File.Exists(Path.Combine(_tempDir, "data.bin")), "data.bin should exist");
    Assert.False(File.Exists(Path.Combine(_tempDir, "data.json")), "data.json should not exist");

    var loaded = svc.Load();
    Assert.Single(loaded.Clips);
    Assert.Equal("hello world", loaded.Clips[0].Raw);
}

[Fact]
public void Load_MigratesLegacyJson_ToEncryptedBin()
{
    // Arrange: write a plain data.json as the legacy format would
    var jsonPath = Path.Combine(_tempDir, "data.json");
    var binPath  = Path.Combine(_tempDir, "data.bin");
    var original = new AppData();
    original.Clips.Add(new ClipEntry { Id = "c2", Raw = "migrated", Text = "migrated" });
    File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(original, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

    // Act
    var loaded = Svc().Load();

    // Assert
    Assert.Equal("migrated", loaded.Clips[0].Raw);
    Assert.True(File.Exists(binPath),   "data.bin should have been created");
    Assert.False(File.Exists(jsonPath), "data.json should have been deleted");
}

[Fact]
public void Load_CleansStaleTemp_AndLoadsFromBin()
{
    var svc     = Svc();
    var binPath = Path.Combine(_tempDir, "data.bin");
    var tmpPath = Path.Combine(_tempDir, "data.bin.tmp");

    // Create a valid data.bin
    var data = new AppData();
    data.Clips.Add(new ClipEntry { Id = "c3", Raw = "stable", Text = "stable" });
    svc.Save(data);

    // Simulate a stale .tmp from a previously interrupted save
    File.WriteAllBytes(tmpPath, new byte[] { 0xFF, 0xFE }); // corrupt garbage

    var loaded = svc.Load();

    Assert.Equal("stable", loaded.Clips[0].Raw);
    Assert.False(File.Exists(tmpPath), "stale .tmp should have been deleted");
}

[Fact]
public void Load_ReturnsEmpty_WhenOnlyStaleTempExists()
{
    var tmpPath = Path.Combine(_tempDir, "data.bin.tmp");
    File.WriteAllBytes(tmpPath, new byte[] { 0xFF });

    var loaded = Svc().Load();

    Assert.Empty(loaded.Clips);
    Assert.False(File.Exists(tmpPath), "orphaned .tmp should have been deleted");
}
```

- [ ] **Step 2: Run to verify they fail**

```
dotnet test ClipMaster.Tests/ClipMaster.Tests.csproj -v minimal
```

Expected: `SaveAndLoad_RoundTripsData` fails because Save/Load still use plaintext JSON; migration/tmp tests fail too.

- [ ] **Step 3: Rewrite Load() in DataService.cs**

Replace the existing `Load()` method with:

```csharp
public AppData Load()
{
    Directory.CreateDirectory(_dataDir);

    // Clean up a stale .tmp from a previously interrupted save
    if (File.Exists(_binTmp))
    {
        try { File.Delete(_binTmp); }
        catch { /* best-effort */ }
    }

    // Normal path: encrypted binary exists
    if (File.Exists(_binFile))
    {
        try
        {
            var cipher = File.ReadAllBytes(_binFile);
            var plain  = System.Security.Cryptography.ProtectedData.Unprotect(
                cipher, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            var json   = System.Text.Encoding.UTF8.GetString(plain);
            return JsonSerializer.Deserialize<AppData>(json, JsonOpts) ?? new AppData();
        }
        catch (Exception ex)
        {
            TraceLog.Write($"Load (bin) FAILED: {ex.GetType().Name}: {ex.Message}");
            return new AppData();
        }
    }

    // Migration path: legacy plaintext JSON exists
    if (File.Exists(_dataFile))
    {
        try
        {
            var json = File.ReadAllText(_dataFile);
            var data = JsonSerializer.Deserialize<AppData>(json, JsonOpts) ?? new AppData();
            Save(data);                // re-save as encrypted
            File.Delete(_dataFile);   // remove plaintext
            return data;
        }
        catch (Exception ex)
        {
            TraceLog.Write($"Load (migration) FAILED: {ex.GetType().Name}: {ex.Message}");
            return new AppData();
        }
    }

    return new AppData();
}
```

- [ ] **Step 4: Rewrite Save() in DataService.cs**

Replace the existing `Save()` method with:

```csharp
public void Save(AppData data)
{
    try
    {
        Directory.CreateDirectory(_dataDir);
        var json   = JsonSerializer.Serialize(data, JsonOpts);
        var plain  = System.Text.Encoding.UTF8.GetBytes(json);
        var cipher = System.Security.Cryptography.ProtectedData.Protect(
            plain, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_binTmp, cipher);
        File.Move(_binTmp, _binFile, overwrite: true);
    }
    catch (Exception ex)
    {
        TraceLog.Write($"Save FAILED: {ex.GetType().Name}: {ex.Message}");
    }
}
```

- [ ] **Step 5: Add using directive at top of DataService.cs**

Ensure this using is present at the top of `DataService.cs` (add if not already present):

```csharp
using System.Text;
```

- [ ] **Step 6: Run tests — expect all pass**

```
dotnet test ClipMaster.Tests/ClipMaster.Tests.csproj -v minimal
```

Expected: all 5 tests PASS.

- [ ] **Step 7: Build the main app**

```
dotnet build ClipMaster.csproj
```

Expected: 0 errors.

- [ ] **Step 8: Commit**

```bash
git add DataService.cs ClipMaster.Tests/DataServiceEncryptionTests.cs
git commit -m "feat(storage): encrypt data at rest using Windows DPAPI"
```

---

## Task 4: Add Export Backup to tray menu

**Files:**
- Modify: `TrayMenu.xaml`
- Modify: `TrayMenu.xaml.cs`
- Modify: `App.xaml.cs`

- [ ] **Step 1: Add the menu item to TrayMenu.xaml**

In `TrayMenu.xaml`, add the Export item and a separator between "Show ClipMaster" and the existing separator. Replace the existing `<!-- Separator -->` block and everything after it with:

```xml
      <!-- Separator -->
      <Border Height="1" Background="#333333" Margin="10,4"/>

      <!-- Export backup -->
      <Border x:Name="ExportItem"
              Background="Transparent"
              CornerRadius="6"
              Padding="14,9"
              Margin="2,0"
              Cursor="Hand"
              MouseLeftButtonUp="ExportItem_Click"
              MouseEnter="Item_MouseEnter"
              MouseLeave="Item_MouseLeave">
        <StackPanel Orientation="Horizontal">
          <TextBlock Text="&#xE792;" FontFamily="Segoe MDL2 Assets"
                     FontSize="13" Foreground="#8e8e8e"
                     VerticalAlignment="Center" Margin="0,0,10,0"/>
          <TextBlock Text="Export backup…"
                     FontFamily="Segoe UI Variable, Segoe UI"
                     FontSize="12.5" FontWeight="Medium"
                     Foreground="#e0e0e0"
                     VerticalAlignment="Center"/>
        </StackPanel>
      </Border>

      <!-- Separator -->
      <Border Height="1" Background="#333333" Margin="10,4"/>

      <!-- Quit -->
      <Border x:Name="QuitItem"
              Background="Transparent"
              CornerRadius="6"
              Padding="14,9"
              Margin="2,0"
              Cursor="Hand"
              MouseLeftButtonUp="QuitItem_Click"
              MouseEnter="Item_MouseEnter"
              MouseLeave="Item_MouseLeave">
        <StackPanel Orientation="Horizontal">
          <TextBlock Text="&#xE711;" FontFamily="Segoe MDL2 Assets"
                     FontSize="13" Foreground="#8e8e8e"
                     VerticalAlignment="Center" Margin="0,0,10,0"/>
          <TextBlock Text="Quit"
                     FontFamily="Segoe UI Variable, Segoe UI"
                     FontSize="12.5" FontWeight="Medium"
                     Foreground="#e05252"
                     VerticalAlignment="Center"/>
        </StackPanel>
      </Border>
    </StackPanel>
  </Border>
</Window>
```

- [ ] **Step 2: Add the event and handler to TrayMenu.xaml.cs**

Add `ExportRequested` event and `ExportItem_Click` handler. Replace the full file content:

```csharp
using System.Windows;
using System.Windows.Media;
using WpfMouseArgs    = System.Windows.Input.MouseEventArgs;
using WpfMouseBtnArgs = System.Windows.Input.MouseButtonEventArgs;

namespace ClipMaster;

public partial class TrayMenu : Window
{
    public event Action? ShowRequested;
    public event Action? ExportRequested;
    public event Action? QuitRequested;

    public TrayMenu()
    {
        InitializeComponent();
    }

    public void ShowNearTray()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var work   = screen.WorkingArea;

        Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = DesiredSize.Width  > 0 ? DesiredSize.Width  : 200;
        var h = DesiredSize.Height > 0 ? DesiredSize.Height : 100;

        Left = Math.Min(cursor.X, work.Right  - w - 8);
        Top  = Math.Min(cursor.Y - h, work.Bottom - h - 8);

        Show();
        Activate();
    }

    private void Window_Deactivated(object sender, EventArgs e) => Hide();

    private void ShowItem_Click(object sender, WpfMouseBtnArgs e)
    {
        Hide();
        ShowRequested?.Invoke();
    }

    private void ExportItem_Click(object sender, WpfMouseBtnArgs e)
    {
        Hide();
        ExportRequested?.Invoke();
    }

    private void QuitItem_Click(object sender, WpfMouseBtnArgs e)
    {
        Hide();
        QuitRequested?.Invoke();
    }

    private static readonly SolidColorBrush HoverBg =
        new(System.Windows.Media.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));

    private void Item_MouseEnter(object sender, WpfMouseArgs e)
    {
        if (sender is System.Windows.Controls.Border b)
            b.Background = HoverBg;
    }

    private void Item_MouseLeave(object sender, WpfMouseArgs e)
    {
        if (sender is System.Windows.Controls.Border b)
            b.Background = System.Windows.Media.Brushes.Transparent;
    }
}
```

- [ ] **Step 3: Add ExportBackup method and wire event in App.xaml.cs**

In `App.xaml.cs`, add `ExportBackup()` method and wire the event in `SetupTray()`.

Add `ExportBackup()` method after the `ToggleWindow()` method:

```csharp
private void OnExportBackup()
{
    var dlg = new Microsoft.Win32.SaveFileDialog
    {
        Title      = "Export ClipMaster Backup",
        FileName   = $"clipmaster-backup-{DateTime.Now:yyyy-MM-dd}",
        DefaultExt = ".json",
        Filter     = "JSON files (*.json)|*.json",
    };
    if (dlg.ShowDialog() != true) return;
    try
    {
        var json = System.Text.Json.JsonSerializer.Serialize(_db, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(dlg.FileName, json);
        TraceLog.Write($"Export OK → {dlg.FileName}");
    }
    catch (Exception ex)
    {
        TraceLog.Write($"Export FAILED: {ex}");
    }
}
```

In `SetupTray()`, add the event wire immediately after the existing two event wires:

```csharp
_trayMenu.ShowRequested  += ToggleWindow;
_trayMenu.ExportRequested += OnExportBackup;   // add this line
_trayMenu.QuitRequested  += Quit;
```

- [ ] **Step 4: Add using directive if needed**

Ensure `using System.IO;` is present at the top of `App.xaml.cs`. (It may already be present via implicit usings — if `dotnet build` complains, add it explicitly.)

- [ ] **Step 5: Build**

```
dotnet build ClipMaster.csproj
```

Expected: 0 errors.

- [ ] **Step 6: Commit**

```bash
git add TrayMenu.xaml TrayMenu.xaml.cs App.xaml.cs
git commit -m "feat(tray): add Export backup menu item"
```

---

## Task 5: Smoke test the full flow manually

- [ ] **Step 1: Run all tests**

```
dotnet test ClipMaster.Tests/ClipMaster.Tests.csproj -v minimal
```

Expected: all 5 tests PASS.

- [ ] **Step 2: Run the app**

```
dotnet run --project ClipMaster.csproj
```

Copy a few things to clipboard. Verify the app picks them up.

- [ ] **Step 3: Check the data file is encrypted**

```
ls -la ~/.clipmaster/
```

Expected: `data.bin` exists; `data.json` does NOT exist.

Open `data.bin` in a text editor. Expected: binary garbage — clipboard content is NOT visible in plaintext.

- [ ] **Step 4: Verify export**

Right-click the tray icon → "Export backup…". Save to Desktop as `test-backup.json`. Open it in a text editor. Expected: plain readable JSON with your clipboard history.

- [ ] **Step 5: Final commit**

```bash
git add -A
git commit -m "chore: verify encrypted storage end-to-end"
```
