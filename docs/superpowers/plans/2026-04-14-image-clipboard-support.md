# Image Clipboard Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Capture images (screenshots, browser copies) from the Windows clipboard, persist them to disk as PNGs, display them in the ClipMaster stack with a large thumbnail card, and allow paste-back via Ctrl+V.

**Architecture:** `ClipboardMonitor` fires a new `NewClipImage` event when an image (not text) is detected on the clipboard, preferring the lossless `"PNG"` clipboard format over CF_DIB. `DataService.AddImageClip()` encodes the `BitmapSource` to PNG, saves it atomically under `~/.clipmaster/images/`, and prunes old image clips independently from text clips. `MainWindow.BuildImageCard()` renders a 140px thumbnail card consistent with the existing dark-theme design.

**Tech Stack:** C# 12 / .NET 8 / WPF; `System.Windows.Media.Imaging` (PngBitmapEncoder, PngBitmapDecoder, BitmapImage — already in PresentationCore.dll, no new packages); no test project (WPF UI app, manual smoke test in Task 7).

---

## File Map

| File | What changes |
|---|---|
| `Models.cs` | Add `IsImage`, `ImagePath`, `ImageWidth`, `ImageHeight`, `ImageBytes` to `ClipEntry`; add `MaxImageHistory` to `AppSettings` |
| `ClipboardMonitor.cs` | Add `NewClipImage` event; add `TryGetClipboardImage()` helper; extend `WndProc` with image detection in `else` branch |
| `DataService.cs` | Add `ImagesDir` field; add `AddImageClip()`, `DeleteImageFile()`, `PruneImageHistory()` |
| `App.xaml.cs` | Wire `NewClipImage`; add `OnNewClipImage()`, `OnPasteImageClip()`, `OnCopyImageClip()`, `TrySetClipboardImageAndHide()` |
| `MainWindow.xaml.cs` | Add `BuildImageCard()`; update `GetFilteredClips()` to hide images during search; update `RenderStack()` to dispatch to `BuildImageCard`; update `RenderSettings()` with Images section and fix Clear History to delete image files |

---

## Task 1: Data Model

**Files:**
- Modify: `Models.cs`

- [ ] **Step 1: Add image fields to `ClipEntry` and `MaxImageHistory` to `AppSettings`**

  Open `Models.cs`. Replace the `ClipEntry` class with:

  ```csharp
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
  ```

  Replace the `AppSettings` class with:

  ```csharp
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
  ```

- [ ] **Step 2: Build to confirm no errors**

  ```
  dotnet build
  ```

  Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

  ```bash
  git add Models.cs
  git commit -m "feat(images): add image fields to ClipEntry and MaxImageHistory to AppSettings"
  ```

---

## Task 2: Clipboard Image Detection

**Files:**
- Modify: `ClipboardMonitor.cs`

- [ ] **Step 1: Add the imaging namespace and `NewClipImage` event**

  At the top of `ClipboardMonitor.cs`, add the imaging using:

  ```csharp
  using System.Runtime.InteropServices;
  using System.Windows;
  using System.Windows.Interop;
  using System.Windows.Media.Imaging;
  ```

  Inside the class body, add the new event after the existing `NewClipText` event:

  ```csharp
  public event Action<string>?       NewClipText;
  public event Action<BitmapSource>? NewClipImage;
  ```

- [ ] **Step 2: Replace `WndProc` with the image-aware version**

  Replace the entire `WndProc` method with:

  ```csharp
  private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
  {
      if (msg == WM_CLIPBOARDUPDATE)
      {
          // Clipboard may still be locked by the source app (e.g. browser copy buttons).
          // Retry a few times with a short delay to handle transient locks.
          for (int attempt = 0; attempt < 5; attempt++)
          {
              try
              {
                  if (System.Windows.Clipboard.ContainsText())
                  {
                      var text = System.Windows.Clipboard.GetText();
                      if (!string.IsNullOrEmpty(text))
                          NewClipText?.Invoke(text);
                  }
                  else
                  {
                      // Text takes priority. Only check for images when no text is present.
                      var image = TryGetClipboardImage();
                      if (image != null)
                          NewClipImage?.Invoke(image);
                  }
                  break;
              }
              catch (COMException)
              {
                  if (attempt < 4)
                      Thread.Sleep(30);
              }
              catch { break; }
          }
      }
      return IntPtr.Zero;
  }
  ```

- [ ] **Step 3: Add `TryGetClipboardImage()` helper**

  Add this private static method to the class (after `WndProc`):

  ```csharp
  private static BitmapSource? TryGetClipboardImage()
  {
      // Prefer the "PNG" registered format — lossless, alpha preserved.
      // Used by Chrome, Edge, and Win+Shift+S Snipping Tool.
      // Let COMException propagate so the WndProc retry loop can handle it.
      if (System.Windows.Clipboard.ContainsData("PNG"))
      {
          var stream = System.Windows.Clipboard.GetData("PNG") as System.IO.MemoryStream;
          if (stream != null)
          {
              try
              {
                  var decoder = new PngBitmapDecoder(
                      stream,
                      BitmapCreateOptions.PreservePixelFormat,
                      BitmapCacheOption.OnLoad);
                  return decoder.Frames[0];
              }
              catch { /* PNG decode failed — fall through to CF_DIB */ }
          }
      }
      // Fall back to CF_DIB (universal fallback — Print Screen, most apps)
      if (System.Windows.Clipboard.ContainsImage())
          return System.Windows.Clipboard.GetImage();
      return null;
  }
  ```

- [ ] **Step 4: Build to confirm no errors**

  ```
  dotnet build
  ```

  Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 5: Commit**

  ```bash
  git add ClipboardMonitor.cs
  git commit -m "feat(images): detect clipboard images with PNG-first fallback strategy"
  ```

---

## Task 3: Image Storage in DataService

**Files:**
- Modify: `DataService.cs`

- [ ] **Step 1: Add the imaging namespace and `ImagesDir` field**

  At the top of `DataService.cs`, add:

  ```csharp
  using System.IO;
  using System.Text.Json;
  using System.Text.RegularExpressions;
  using System.Windows.Media.Imaging;
  ```

  Inside the `DataService` class, add `ImagesDir` after the existing `DataFile` field:

  ```csharp
  private static readonly string DataDir   = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clipmaster");
  private static readonly string DataFile  = Path.Combine(DataDir, "data.json");
  private static readonly string ImagesDir = Path.Combine(DataDir, "images");
  ```

- [ ] **Step 2: Add `DeleteImageFile()` helper**

  Add this public method to `DataService` (after `Save()`):

  ```csharp
  public void DeleteImageFile(string? relPath)
  {
      if (string.IsNullOrEmpty(relPath)) return;
      try
      {
          var abs = Path.Combine(DataDir, relPath);
          if (File.Exists(abs)) File.Delete(abs);
      }
      catch (Exception ex) { TraceLog.Write($"DeleteImageFile failed: {ex.Message}"); }
  }
  ```

- [ ] **Step 3: Add `PruneImageHistory()` method**

  Add this public method (after `DeleteImageFile()`):

  ```csharp
  public void PruneImageHistory(AppData data)
  {
      var max          = data.Settings.MaxImageHistory;
      var pinnedImgs   = data.Clips.Where(c => c.IsImage && c.Pinned).ToList();
      var unpinnedImgs = data.Clips.Where(c => c.IsImage && !c.Pinned).Take(max).ToList();
      var kept         = pinnedImgs.Concat(unpinnedImgs).Select(c => c.Id).ToHashSet();

      var toDelete = data.Clips.Where(c => c.IsImage && !kept.Contains(c.Id)).ToList();
      foreach (var old in toDelete)
      {
          data.Clips.Remove(old);
          DeleteImageFile(old.ImagePath);
      }
  }
  ```

- [ ] **Step 4: Add `AddImageClip()` method**

  Add this public method (after `PruneImageHistory()`):

  ```csharp
  public ClipEntry? AddImageClip(AppData data, BitmapSource image)
  {
      try
      {
          Directory.CreateDirectory(ImagesDir);

          // Encode BitmapSource → PNG bytes
          var encoder = new PngBitmapEncoder();
          encoder.Frames.Add(BitmapFrame.Create(image));
          byte[] bytes;
          using (var ms = new System.IO.MemoryStream())
          {
              encoder.Save(ms);
              bytes = ms.ToArray();
          }

          var id      = $"img_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Random.Shared.Next(10000, 99999)}";
          var relPath = $"images/{id}.png";
          var absPath = Path.Combine(DataDir, relPath);
          var tmpPath = absPath + ".tmp";

          // Atomic write: write to .tmp then rename
          File.WriteAllBytes(tmpPath, bytes);
          File.Move(tmpPath, absPath, overwrite: true);

          var entry = new ClipEntry
          {
              Id          = id,
              IsImage     = true,
              ImagePath   = relPath,
              ImageWidth  = image.PixelWidth,
              ImageHeight = image.PixelHeight,
              ImageBytes  = bytes.LongLength,
              CopiedAt    = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
              CopyCount   = 1,
          };
          data.Clips.Insert(0, entry);

          // Prune image clips independently from text clips
          PruneImageHistory(data);

          Save(data);
          return entry;
      }
      catch (Exception ex)
      {
          TraceLog.Write($"AddImageClip FAILED: {ex.GetType().Name}: {ex.Message}");
          return null;
      }
  }
  ```

- [ ] **Step 5: Build to confirm no errors**

  ```
  dotnet build
  ```

  Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 6: Commit**

  ```bash
  git add DataService.cs
  git commit -m "feat(images): add AddImageClip, PruneImageHistory, DeleteImageFile to DataService"
  ```

---

## Task 4: Wire Image Events in App.xaml.cs

**Files:**
- Modify: `App.xaml.cs`

- [ ] **Step 1: Add missing namespaces**

  Ensure the top of `App.xaml.cs` has these usings (add any missing):

  ```csharp
  using System.Drawing;
  using System.IO;
  using System.Windows;
  using System.Windows.Forms;
  using System.Windows.Media.Imaging;
  using Application = System.Windows.Application;
  ```

- [ ] **Step 2: Subscribe to `NewClipImage` in `SourceInitialized`**

  In `OnStartup`, inside the `_window.SourceInitialized` handler, add the new subscription directly after the existing `_clip.NewClipText += OnNewClip;` line:

  ```csharp
  _clip.NewClipText  += OnNewClip;
  _clip.NewClipImage += OnNewClipImage;   // ← add this line
  ```

- [ ] **Step 3: Add `OnNewClipImage()`**

  Add this method after the existing `OnNewClip()` method:

  ```csharp
  private void OnNewClipImage(BitmapSource image)
  {
      if (_suppressing) return;
      var entry = _data.AddImageClip(_db, image);
      if (entry == null) return;
      Dispatcher.Invoke(() =>
      {
          if (_window?.IsVisible == true)
              _window.RefreshClips(_db.Clips);
      });
  }
  ```

- [ ] **Step 4: Add `TrySetClipboardImageAndHide()`**

  Add this method after the existing `TrySetClipboardAndHide(string text)` method:

  ```csharp
  private bool TrySetClipboardImageAndHide(string relPath)
  {
      _suppressing = true;
      try
      {
          var abs = Path.Combine(
              Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
              ".clipmaster", relPath);
          var bmp = new BitmapImage(new Uri(abs));
          Dispatcher.Invoke(() =>
          {
              for (int attempt = 0; attempt < 5; attempt++)
              {
                  try
                  {
                      System.Windows.Clipboard.SetImage(bmp);
                      _window?.Hide();
                      return;
                  }
                  catch (System.Runtime.InteropServices.COMException)
                  {
                      if (attempt < 4) Thread.Sleep(30);
                  }
              }
              TraceLog.Write("SetImage failed after 5 attempts — clipboard locked");
          });
          return true;
      }
      catch (Exception ex)
      {
          TraceLog.Write($"TrySetClipboardImageAndHide FAILED: {ex}");
          return false;
      }
      finally { _suppressing = false; }
  }
  ```

- [ ] **Step 5: Add `OnPasteImageClip()` and `OnCopyImageClip()`**

  Add these two public methods after `OnPasteClip()`:

  ```csharp
  public void OnPasteImageClip(string clipId)
  {
      var clip = _db.Clips.FirstOrDefault(c => c.Id == clipId);
      if (clip?.ImagePath == null) return;
      if (!TrySetClipboardImageAndHide(clip.ImagePath)) return;
      Task.Run(() => PasteService.Paste(150));
  }

  public void OnCopyImageClip(string clipId)
  {
      var clip = _db.Clips.FirstOrDefault(c => c.Id == clipId);
      if (clip?.ImagePath == null) return;
      try
      {
          var abs = Path.Combine(
              Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
              ".clipmaster", clip.ImagePath);
          var bmp = new BitmapImage(new Uri(abs));
          _suppressing = true;
          try { Dispatcher.Invoke(() => System.Windows.Clipboard.SetImage(bmp)); }
          finally { _suppressing = false; }
      }
      catch (Exception ex) { TraceLog.Write($"OnCopyImageClip FAILED: {ex}"); }
  }
  ```

- [ ] **Step 6: Build to confirm no errors**

  ```
  dotnet build
  ```

  Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 7: Commit**

  ```bash
  git add App.xaml.cs
  git commit -m "feat(images): wire NewClipImage event, add paste/copy handlers for image clips"
  ```

---

## Task 5: Image Clip Card UI

**Files:**
- Modify: `MainWindow.xaml.cs`

- [ ] **Step 1: Add the imaging namespace**

  At the top of `MainWindow.xaml.cs`, ensure `System.Windows.Media.Imaging` and `System.IO` are present. Add after the existing usings if missing:

  ```csharp
  using System.IO;
  using System.Windows.Media.Imaging;
  ```

- [ ] **Step 2: Update `GetFilteredClips()` to hide images during text search**

  Locate the `GetFilteredClips()` method. Replace the search filter block (the `if (!string.IsNullOrWhiteSpace(_search))` section) with:

  ```csharp
  if (!string.IsNullOrWhiteSpace(_search))
  {
      var q = _search.ToLowerInvariant();
      // Image clips have no text content — hide them entirely during search
      clips = clips.Where(c =>
          !c.IsImage &&
          (c.Text.ToLowerInvariant().Contains(q) ||
           c.Raw.ToLowerInvariant().Contains(q)));
  }
  ```

- [ ] **Step 3: Update `RenderStack()` to dispatch to `BuildImageCard`**

  Replace the `RenderStack()` method with:

  ```csharp
  private void RenderStack()
  {
      ClipList.Children.Clear();
      var clips = GetFilteredClips();
      for (int i = 0; i < clips.Count; i++)
      {
          var card = clips[i].IsImage
              ? BuildImageCard(clips[i], i + 1)
              : BuildClipCard(clips[i], i + 1);
          ClipList.Children.Add(card);
      }
      RenderTagFilterChips();
  }
  ```

- [ ] **Step 4: Add `BuildImageCard()`**

  Add this private method immediately after `BuildClipCard()` (after its closing brace, before `MakeBadge`):

  ```csharp
  private UIElement BuildImageCard(ClipEntry clip, int rank)
  {
      double opacity = rank switch { 1 => 1.0, 2 => 0.85, 3 => 0.70, _ => 0.55 };
      string rankBg  = rank switch { 1 => "#7c6af7", 2 => "#5548aa", 3 => "#3d3880", _ => "#2a2460" };

      // Rank badge
      var rankBadge = new Border
      {
          Style      = (Style)FindResource("RankBadge"),
          Background = BrushFromHex(rankBg),
          Child      = new TextBlock
          {
              Text                = rank.ToString(),
              FontSize            = 10,
              FontWeight          = FontWeights.Bold,
              Foreground          = WpfBrushes.White,
              HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
              VerticalAlignment   = VerticalAlignment.Center,
          },
      };

      // Badges row
      var badgesPanel = new StackPanel
      {
          Orientation       = WpfOrientation.Horizontal,
          Margin            = new Thickness(6, 0, 0, 0),
          VerticalAlignment = VerticalAlignment.Center,
      };
      badgesPanel.Children.Add(MakeBadge("image", "#1a2e3a", "#5bb8d4"));
      if (clip.Pinned) badgesPanel.Children.Add(MakeBadge("pinned", "#3d3880", "#a89ef7"));

      var metaTb = new TextBlock
      {
          Text              = TimeAgo(clip.CopiedAt),
          FontSize          = 10,
          Foreground        = (WpfBrush)FindResource("Text3Brush"),
          Margin            = new Thickness(6, 0, 0, 0),
          VerticalAlignment = VerticalAlignment.Center,
      };

      var header = new StackPanel
      {
          Orientation = WpfOrientation.Horizontal,
          Margin      = new Thickness(0, 0, 0, 8),
      };
      header.Children.Add(rankBadge);
      header.Children.Add(badgesPanel);
      header.Children.Add(metaTb);

      // Thumbnail — loaded with DecodePixelWidth so WPF only decodes a scaled version
      System.Windows.Controls.Image thumbnail;
      if (clip.ImagePath != null)
      {
          var abs = Path.Combine(
              Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
              ".clipmaster", clip.ImagePath);
          var bmp = new BitmapImage();
          bmp.BeginInit();
          bmp.UriSource        = new Uri(abs);
          bmp.DecodePixelWidth = 420;
          bmp.CacheOption      = BitmapCacheOption.OnLoad;
          bmp.EndInit();
          thumbnail = new System.Windows.Controls.Image
          {
              Source              = bmp,
              Height              = 140,
              Stretch             = System.Windows.Media.Stretch.UniformToFill,
              HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
          };
      }
      else
      {
          thumbnail = new System.Windows.Controls.Image { Height = 140 };
      }

      var thumbBorder = new Border
      {
          CornerRadius = new CornerRadius(6),
          ClipToBounds = true,
          Margin       = new Thickness(0, 0, 0, 8),
          Child        = thumbnail,
      };

      // Action buttons
      var pasteBtn = CardBtn("Paste", "PasteBtn");
      var copyBtn  = CardBtn("Copy",  "ActionBtn");
      var pinBtn   = CardBtn(clip.Pinned ? "Unpin" : "Pin", "ActionBtn");
      var tagBtn   = CardBtn("Tag",   "ActionBtn");
      var delBtn   = CardBtn("✕",     "DangerBtn");

      pasteBtn.Click += (_, _) => ((App)WpfApp.Current).OnPasteImageClip(clip.Id);
      copyBtn.Click  += (_, _) =>
      {
          ((App)WpfApp.Current).OnCopyImageClip(clip.Id);
          ShowToast("Copied");
      };
      pinBtn.Click += (_, _) =>
      {
          clip.Pinned = !clip.Pinned;
          _svc.Save(_db);
          RenderStack();
      };
      tagBtn.Click += (_, _) => OpenTagDialog(clip);
      delBtn.Click += (_, _) =>
      {
          _db.Clips.Remove(clip);
          _svc.DeleteImageFile(clip.ImagePath);
          _svc.Save(_db);
          RenderStack();
      };

      var actionsPanel = new StackPanel { Orientation = WpfOrientation.Horizontal };
      actionsPanel.Children.Add(pasteBtn);
      actionsPanel.Children.Add(copyBtn);
      actionsPanel.Children.Add(pinBtn);
      actionsPanel.Children.Add(tagBtn);
      actionsPanel.Children.Add(delBtn);

      // Dimensions / size label
      var sizeLabel = clip.ImageBytes >= 1_048_576
          ? $"{clip.ImageBytes / 1_048_576.0:F1} MB"
          : $"{clip.ImageBytes / 1024.0:F0} KB";
      var dimLabel = new TextBlock
      {
          Text              = $"{clip.ImageWidth} × {clip.ImageHeight} · PNG · {sizeLabel}",
          FontSize          = 9,
          Foreground        = (WpfBrush)FindResource("Text3Brush"),
          VerticalAlignment = VerticalAlignment.Center,
      };

      // Footer: actions left, dimensions right
      var footer = new DockPanel { LastChildFill = false };
      DockPanel.SetDock(dimLabel, Dock.Right);
      footer.Children.Add(actionsPanel);
      footer.Children.Add(dimLabel);

      var cardContent = new StackPanel();
      cardContent.Children.Add(header);
      cardContent.Children.Add(thumbBorder);
      cardContent.Children.Add(footer);

      var card = new Border
      {
          Style   = (Style)FindResource("ClipCard"),
          Opacity = opacity,
          Child   = cardContent,
      };

      card.MouseEnter += (_, _) => card.Background = BrushFromHex("#282828");
      card.MouseLeave += (_, _) => card.Background = (WpfBrush)FindResource("SurfaceBrush");

      // Double-click on card body → paste
      card.MouseLeftButtonDown += (_, e) =>
      {
          if (e.ClickCount == 2 && e.OriginalSource is not WpfButton)
              ((App)WpfApp.Current).OnPasteImageClip(clip.Id);
      };

      // Right-click context menu
      card.MouseRightButtonUp += (_, _) =>
      {
          var menu = new ContextMenu();
          MenuItem MI(string hdr, Action action)
          {
              var mi = new MenuItem { Header = hdr };
              mi.Click += (_, _) => action();
              return mi;
          }
          menu.Items.Add(MI("Paste", () => ((App)WpfApp.Current).OnPasteImageClip(clip.Id)));
          menu.Items.Add(MI("Copy",  () => { ((App)WpfApp.Current).OnCopyImageClip(clip.Id); ShowToast("Copied"); }));
          menu.Items.Add(MI(clip.Pinned ? "Unpin" : "Pin", () => { clip.Pinned = !clip.Pinned; _svc.Save(_db); RenderStack(); }));
          menu.Items.Add(MI("Manage tags…", () => OpenTagDialog(clip)));
          menu.Items.Add(new Separator());
          menu.Items.Add(MI("Delete", () =>
          {
              _db.Clips.Remove(clip);
              _svc.DeleteImageFile(clip.ImagePath);
              _svc.Save(_db);
              RenderStack();
          }));
          card.ContextMenu = menu;
          menu.IsOpen      = true;
      };

      return card;
  }
  ```

- [ ] **Step 5: Build to confirm no errors**

  ```
  dotnet build
  ```

  Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 6: Commit**

  ```bash
  git add MainWindow.xaml.cs
  git commit -m "feat(images): add BuildImageCard, update RenderStack and GetFilteredClips for image clips"
  ```

---

## Task 6: Settings — Max Image History + Fix Clear History

**Files:**
- Modify: `MainWindow.xaml.cs`

- [ ] **Step 1: Add Images section to `RenderSettings()`**

  In `RenderSettings()`, locate the `// Data` section header comment. Insert a new Images section immediately before it:

  ```csharp
  // Images
  SectionHeader("IMAGES");

  var maxImgBox = StyledInput(s.MaxImageHistory.ToString(), 80);
  SettingRow("Max image history", "Image clips to keep on disk", maxImgBox);

  // Data    ← existing section, keep as-is below this point
  ```

- [ ] **Step 2: Fix Clear History to delete image files**

  In `RenderSettings()`, find the `clearBtn.Click` handler. Replace the line that filters clips:

  ```csharp
  // Old:
  _db.Clips = _db.Clips.Where(c => c.Pinned).ToList();

  // New — delete image files before removing clips:
  var imagesToDelete = _db.Clips.Where(c => !c.Pinned && c.IsImage).ToList();
  foreach (var img in imagesToDelete)
      _svc.DeleteImageFile(img.ImagePath);
  _db.Clips = _db.Clips.Where(c => c.Pinned).ToList();
  ```

- [ ] **Step 3: Wire `MaxImageHistory` into the Save button handler**

  In `RenderSettings()`, find the `saveBtn.Click` handler. Add the `MaxImageHistory` save line after the existing settings assignments and call `PruneImageHistory` to enforce the new limit immediately:

  ```csharp
  saveBtn.Click += (_, _) =>
  {
      s.Hotkey          = hotkeyBox.Text.Trim();
      s.MaxHistory      = int.TryParse(maxBox.Text, out var m) ? m : 500;
      s.MaskPasswords   = maskCb.IsChecked == true;
      s.AutoApplyRules  = autoRulesCb.IsChecked == true;
      s.RunOnStartup    = startupCb.IsChecked == true;
      s.MaxImageHistory = int.TryParse(maxImgBox.Text, out var mi) ? Math.Max(1, mi) : 50;
      _svc.PruneImageHistory(_db);   // enforce new limit immediately
      _svc.Save(_db);
      ((App)WpfApp.Current).RehostHotkey(s.Hotkey);
      StartupService.SetRunOnStartup(s.RunOnStartup);
      ShowToast("Settings saved");
  };
  ```

- [ ] **Step 4: Build to confirm no errors**

  ```
  dotnet build
  ```

  Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 5: Commit**

  ```bash
  git add MainWindow.xaml.cs
  git commit -m "feat(images): add Max image history setting, fix Clear History to delete image files"
  ```

---

## Task 7: Smoke Test

No automated test infrastructure exists. Verify manually:

- [ ] **Step 1: Run the app**

  ```
  dotnet run
  ```

  ClipMaster tray icon appears. No crash on startup.

- [ ] **Step 2: Screenshot capture via Win+Shift+S**

  Press Win+Shift+S, drag to select a region, confirm capture. Open ClipMaster (Ctrl+\`).

  Expected: An image card appears at rank 1 with a 140px thumbnail of the captured region, an `image` teal badge, and a `W × H · PNG · X KB` label in the footer.

- [ ] **Step 3: Browser image copy**

  Right-click any image in a browser → Copy image. Open ClipMaster.

  Expected: Image card appears at rank 1 with a thumbnail.

- [ ] **Step 4: Text still works**

  Copy any text. Open ClipMaster.

  Expected: Text card appears at rank 1; the image card drops to rank 2.

- [ ] **Step 5: Paste image into an app**

  Click **Paste** on an image card. Switch to Word, Slack, or a browser address bar that accepts images.

  Expected: The image is pasted via Ctrl+V simulation.

- [ ] **Step 6: Copy button**

  Click **Copy** on an image card. Open Paint (mspaint), Ctrl+V.

  Expected: Image is pasted. ClipMaster window stays open.

- [ ] **Step 7: Text search hides image clips**

  Open ClipMaster with mixed text + image clips in the stack. Type in the search box.

  Expected: Image cards disappear from the list; only matching text clips are shown. Clearing the search box restores image cards.

- [ ] **Step 8: Pin and delete**

  Pin an image clip. Verify the `pinned` badge appears. Click ✕ on an unpinned image clip. Open `~/.clipmaster/images/` in File Explorer.

  Expected: The deleted clip's `.png` file is gone. Pinned clip's `.png` remains.

- [ ] **Step 9: Settings — Max image history**

  Open Settings → Images. Change Max image history to `1`. Save.

  Expected: All but the most recent (and pinned) image clips are removed from the stack and their `.png` files are deleted from `~/.clipmaster/images/`.

- [ ] **Step 10: Restart persistence**

  Quit and relaunch ClipMaster. Open the stack.

  Expected: Image clips are still present with correct thumbnails.

- [ ] **Step 11: Final commit**

  ```bash
  git add -A
  git status
  # confirm only expected files changed (none should be — all changes committed in earlier tasks)
  ```
