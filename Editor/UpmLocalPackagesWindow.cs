// Assets/Editor/UpmLocalPackagesWindow.cs
// Unity 2021+; UI Toolkit; Newtonsoft.Json (com.unity.nuget.newtonsoft-json)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.UIElements;
using Newtonsoft.Json.Linq;
using UnityEditor.UIElements;

// ReSharper disable InconsistentNaming

public class UpmLocalPackagesWindow : EditorWindow
{
    [MenuItem("UniGame/Tools/UPM Local Packages")]
    public static void ShowWindow()
    {
        var wnd = GetWindow<UpmLocalPackagesWindow>();
        wnd.titleContent = new GUIContent("UPM Local Packages");
        wnd.minSize = new Vector2(900, 560);
        wnd.Show();
    }

    // ===== Persistent state =====
    [Serializable]
    [FilePath("ProjectSettings/UPMLocalPackagesState.json", FilePathAttribute.Location.ProjectFolder)]
    class State : ScriptableSingleton<State>
    {
        // Пути корней для сканирования: храним ОТНОСИТЕЛЬНО корня проекта (как и раньше)
        public List<string> searchRoots = new List<string>();
        public bool showOnlyNotInstalled = false;

        public string lastSearchSummary = "";

        // Выбор пакетов для батч-установки: храним ОТНОСИТЕЛЬНО Packages (именно такая строка попадет в file:)
        public List<string> selectedPackageRelFromPackages = new List<string>();

        void OnEnable() => hideFlags |= HideFlags.DontSave;
        public void SaveNow() => Save(true);
    }

    // ===== Data model =====
    [Serializable]
    class PackageRow
    {
        public string PathAbs; // абсолютный путь к папке пакета
        public string PathRelProj; // относительный от корня проекта (для UI)
        public string PathRelForManifest; // относительный от Packages/ (для manifest.json → file:...)
        public string Name;
        public string DisplayName;
        public string Version;
        public string Description;
        public List<string> Keywords;
        public bool Installed;
        public string InstalledVersion;
        public string ManifestValue; // e.g. "file:../game.packages/xxx" или "1.2.3" или git url
        public bool InstalledFromLocal; // ManifestValue starts with "file:"
    }

    // UI refs
    private ListView _packagesList;
    private TextField _detailsField;
    private Button _openFolderBtn;
    private Button _installSingleBtn;
    private Button _uninstallBtn;

    // settings tab
    private ListView _pathsList;
    private Button _addPathBtn, _removePathBtn;

    // top bar
    private Button _scanBtn, _installBtn;
    private Toggle _onlyNotInstalledToggle;
    private Label _summaryLabel;

    // data
    private List<PackageRow> _allFound = new();
    private Dictionary<string, string> _manifestDeps = new(); // name -> value from manifest

    // UPM requests
    private readonly List<AddRequest> _pendingAdds = new();
    private readonly List<RemoveRequest> _pendingRemoves = new();

    // tabs
    private ToolbarToggle _tabDetails, _tabSettings;
    private VisualElement _panelDetails, _panelSettings;

    // selection
    private PackageRow _currentSelection;

    // ===== Paths helpers =====
    static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    static string PackagesRoot => Path.Combine(ProjectRoot, "Packages");

    static string ToAbsoluteFromProject(string maybeRel)
    {
        if (string.IsNullOrEmpty(maybeRel)) return maybeRel;
        if (Path.IsPathRooted(maybeRel)) return Path.GetFullPath(maybeRel);
        return Path.GetFullPath(Path.Combine(ProjectRoot, maybeRel));
    }

    static string ToRelativeFrom(string baseDirAbs, string targetAbs)
    {
        var baseDir = Path.GetFullPath(baseDirAbs);
        var target = Path.GetFullPath(targetAbs);
        try
        {
            var relUri = new Uri(baseDir.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? baseDir
                    : baseDir + Path.DirectorySeparatorChar)
                .MakeRelativeUri(new Uri(target));
            return Uri.UnescapeDataString(relUri.ToString()).Replace('\\', '/');
        }
        catch
        {
            // fallback
            if (target.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                return target.Substring(baseDir.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
            return target.Replace('\\', '/');
        }
    }

    static string ToProjectRelative(string absPath) => ToRelativeFrom(ProjectRoot, absPath);

    static string ToPackagesRelative(string absPath) => ToRelativeFrom(PackagesRoot, absPath);

    void OnDisable()
    {
        // на всякий случай фиксируем все последние изменения
        State.instance.SaveNow();
    }

    private void CreateGUI()
    {
        var root = rootVisualElement;
        root.style.paddingLeft = 8;
        root.style.paddingRight = 8;
        root.style.paddingTop = 8;
        root.style.paddingBottom = 8;

        // ----- Top bar -----
        var topBar = new VisualElement
            { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 6 } };
        root.Add(topBar);

        _scanBtn = new Button(ScanAndRefresh) { text = "Scan" };
        _scanBtn.style.marginRight = 8;
        topBar.Add(_scanBtn);

        _installBtn = new Button(InstallSelectedBatch) { text = "Update Packages (add selected)" };
        _installBtn.style.marginRight = 8;
        topBar.Add(_installBtn);

        _onlyNotInstalledToggle = new Toggle("Show only not installed")
        {
            value = State.instance.showOnlyNotInstalled
        };
        _onlyNotInstalledToggle.style.marginRight = 8;
        _onlyNotInstalledToggle.RegisterValueChangedCallback(evt =>
        {
            State.instance.showOnlyNotInstalled = evt.newValue;
            State.instance.SaveNow();
            RefreshPackagesListView();
        });
        topBar.Add(_onlyNotInstalledToggle);

        _summaryLabel = new Label(State.instance.lastSearchSummary);
        _summaryLabel.style.color = new Color(1, 1, 1, 0.7f);
        topBar.Add(_summaryLabel);

        // ----- Main split: Left list | Right tabs -----
        var split = new TwoPaneSplitView(0, 560, TwoPaneSplitViewOrientation.Horizontal) { style = { flexGrow = 1 } };
        root.Add(split);

        // LEFT: packages list (full height)
        var left = new VisualElement { style = { flexGrow = 1 } };
        split.Add(left);

        _packagesList = new ListView
        {
            fixedItemHeight = 24,
            showBorder = true,
            selectionType = SelectionType.Single
        };
        _packagesList.makeItem = MakePackageRow;
        _packagesList.bindItem = BindPackageRow;
        _packagesList.itemsSource = GetFiltered();
        _packagesList.onSelectionChange += OnSelectPackage;
        left.Add(_packagesList);

        // RIGHT: tabs (Details / Settings)
        var right = new VisualElement { style = { flexGrow = 1, paddingLeft = 8 } };
        split.Add(right);

        var tabsBar = new Toolbar();
        right.Add(tabsBar);

        _tabDetails = new ToolbarToggle { text = "Details" };
        _tabSettings = new ToolbarToggle { text = "Settings" };

        _tabDetails.RegisterValueChangedCallback(evt =>
        {
            if (evt.newValue)
            {
                _tabSettings.SetValueWithoutNotify(false);
                ShowPanel(_panelDetails);
            }
            else if (!_tabSettings.value)
            {
                _tabDetails.SetValueWithoutNotify(true);
            }
        });
        _tabSettings.RegisterValueChangedCallback(evt =>
        {
            if (evt.newValue)
            {
                _tabDetails.SetValueWithoutNotify(false);
                ShowPanel(_panelSettings);
            }
            else if (!_tabDetails.value)
            {
                _tabSettings.SetValueWithoutNotify(true);
            }
        });

        tabsBar.Add(_tabDetails);
        tabsBar.Add(_tabSettings);

        var tabsContent = new VisualElement { style = { flexGrow = 1, marginTop = 6 } };
        right.Add(tabsContent);

        // Details panel
        _panelDetails = new VisualElement { style = { flexGrow = 1 } };

        _detailsField = new TextField("Package info") { multiline = true, isReadOnly = true };
        _detailsField.style.flexGrow = 1;
        _detailsField.style.whiteSpace = WhiteSpace.Normal;
        _panelDetails.Add(_detailsField);

        var detailsButtonsRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 6 } };

        _openFolderBtn = new Button(OpenSelectedFolder) { text = "Open Folder" };
        _openFolderBtn.style.marginRight = 6;
        _openFolderBtn.SetEnabled(false);
        detailsButtonsRow.Add(_openFolderBtn);

        _installSingleBtn = new Button(InstallSelectedSingle) { text = "Install (add to manifest)" };
        _installSingleBtn.style.marginRight = 6;
        _installSingleBtn.SetEnabled(false);
        detailsButtonsRow.Add(_installSingleBtn);

        _uninstallBtn = new Button(UninstallSelected) { text = "Uninstall (remove from manifest)" };
        _uninstallBtn.SetEnabled(false);
        detailsButtonsRow.Add(_uninstallBtn);

        _panelDetails.Add(detailsButtonsRow);
        tabsContent.Add(_panelDetails);

        // Settings panel
        _panelSettings = new VisualElement { style = { flexGrow = 1 } };
        tabsContent.Add(_panelSettings);

        var pathsTitle = new Label("Folders to scan (local UPM/npm packages)");
        pathsTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
        pathsTitle.style.marginBottom = 4;
        _panelSettings.Add(pathsTitle);

        _pathsList = new ListView(State.instance.searchRoots, itemHeight: 20, makeItem: () => new Label(),
            bindItem: (ve, i) => { (ve as Label).text = State.instance.searchRoots[i]; })
        {
            selectionType = SelectionType.Single,
            showBorder = true
        };
        _pathsList.style.flexGrow = 1;
        _panelSettings.Add(_pathsList);

        var pathButtons = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 6 } };
        _panelSettings.Add(pathButtons);

        _addPathBtn = new Button(() =>
        {
            var picked = EditorUtility.OpenFolderPanel("Select root folder to scan for package.json", ProjectRoot, "");
            if (!string.IsNullOrEmpty(picked))
            {
                var relProj = ToProjectRelative(picked);
                if (!State.instance.searchRoots.Contains(relProj))
                {
                    State.instance.searchRoots.Add(relProj);
                    State.instance.SaveNow();
                    _pathsList.itemsSource = null;
                    _pathsList.itemsSource = State.instance.searchRoots;
                    _pathsList.Rebuild();
                }
            }
        }) { text = "Add Path..." };
        _addPathBtn.style.marginRight = 6;
        pathButtons.Add(_addPathBtn);

        _removePathBtn = new Button(() =>
        {
            var idx = _pathsList.selectedIndex;
            if (idx >= 0 && idx < State.instance.searchRoots.Count)
            {
                State.instance.searchRoots.RemoveAt(idx);
                State.instance.SaveNow();
                _pathsList.itemsSource = null;
                _pathsList.itemsSource = State.instance.searchRoots;
                _pathsList.Rebuild();
            }
        }) { text = "Remove Selected" };
        pathButtons.Add(_removePathBtn);

        // Default tab
        _tabDetails.SetValueWithoutNotify(true);
        _tabSettings.SetValueWithoutNotify(false);
        ShowPanel(_panelDetails);

        // auto-scan on open
        EditorApplication.delayCall += ScanAndRefresh;
    }

    void ShowPanel(VisualElement panelToShow)
    {
        _panelDetails.style.display = (panelToShow == _panelDetails) ? DisplayStyle.Flex : DisplayStyle.None;
        _panelSettings.style.display = (panelToShow == _panelSettings) ? DisplayStyle.Flex : DisplayStyle.None;
    }

    // ----- Packages list row -----
    VisualElement MakePackageRow()
    {
        var row = new VisualElement
        {
            style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingLeft = 6, paddingRight = 6 }
        };

        var toggle = new Toggle { name = "sel" };
        toggle.style.width = 20;
        toggle.style.marginRight = 8;
        toggle.userData = null;
        row.Add(toggle);

        var name = new Label { name = "name" };
        name.style.minWidth = 220;
        name.style.marginRight = 8;
        row.Add(name);

        var ver = new Label { name = "ver" };
        ver.style.minWidth = 80;
        ver.style.marginRight = 8;
        row.Add(ver);

        var installed = new Label { name = "inst" };
        installed.style.minWidth = 200;
        installed.style.marginRight = 8;
        row.Add(installed);

        var path = new Label { name = "path" };
        path.style.flexGrow = 1;
        path.style.color = new Color(1, 1, 1, 0.6f);
        row.Add(path);

        return row;
    }

    void BindPackageRow(VisualElement ve, int index)
    {
        var data = (List<PackageRow>)_packagesList.itemsSource;
        if (index < 0 || index >= data.Count) return;
        var p = data[index];

        var toggle = ve.Q<Toggle>("sel");

        if (toggle.userData is EventCallback<ChangeEvent<bool>> oldCb)
            toggle.UnregisterValueChangedCallback(oldCb);

        if (p.Installed)
        {
            toggle.SetValueWithoutNotify(true);
            toggle.SetEnabled(false);
            toggle.userData = null;
        }
        else
        {
            var isSelected = State.instance.selectedPackageRelFromPackages.Contains(p.PathRelForManifest);
            toggle.SetValueWithoutNotify(isSelected);
            toggle.SetEnabled(true);

            EventCallback<ChangeEvent<bool>> cb = evt =>
            {
                if (evt.newValue)
                {
                    if (!State.instance.selectedPackageRelFromPackages.Contains(p.PathRelForManifest))
                        State.instance.selectedPackageRelFromPackages.Add(p.PathRelForManifest);
                }
                else
                {
                    State.instance.selectedPackageRelFromPackages.Remove(p.PathRelForManifest);
                }

                State.instance.SaveNow();
            };
            toggle.RegisterValueChangedCallback(cb);
            toggle.userData = cb;
        }

        ve.Q<Label>("name").text = string.IsNullOrEmpty(p.DisplayName) ? p.Name : p.DisplayName;
        ve.Q<Label>("ver").text = p.Version ?? "-";

        var inst = ve.Q<Label>("inst");
        if (p.Installed)
        {
            var src = p.InstalledFromLocal ? "local" : "registry";
            var verTxt = string.IsNullOrEmpty(p.InstalledVersion) ? p.ManifestValue : p.InstalledVersion;
            inst.text = $"Installed ({src}: {verTxt})";
            inst.style.color = new Color(0.5f, 1f, 0.5f);
        }
        else
        {
            inst.text = "Not installed";
            inst.style.color = new Color(1f, 0.6f, 0.3f);
        }

        // Показываем путь, который реально пойдет в manifest: относительный от Packages/
        ve.Q<Label>("path").text = p.PathRelForManifest;
    }

    void OnSelectPackage(IEnumerable<object> objs)
    {
        var sel = objs?.FirstOrDefault() as PackageRow;
        _currentSelection = sel;

        if (sel == null)
        {
            _detailsField.value = "";
            _openFolderBtn?.SetEnabled(false);
            _installSingleBtn?.SetEnabled(false);
            _uninstallBtn?.SetEnabled(false);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Name:        {sel.Name}");
        if (!string.IsNullOrEmpty(sel.DisplayName)) sb.AppendLine($"DisplayName: {sel.DisplayName}");
        sb.AppendLine($"Version:     {sel.Version}");
        if (!string.IsNullOrEmpty(sel.Description)) sb.AppendLine($"Description: {sel.Description}");
        if (sel.Keywords != null && sel.Keywords.Count > 0)
            sb.AppendLine($"Tags:        {string.Join(", ", sel.Keywords)}");
        sb.AppendLine($"Path (abs):  {sel.PathAbs}");
        sb.AppendLine($"Path (proj): {sel.PathRelProj}");
        sb.AppendLine($"Path (for manifest): {sel.PathRelForManifest}");
        sb.AppendLine(
            $"Installed:   {(sel.Installed ? $"Yes ({(sel.InstalledFromLocal ? "local" : "registry")}: {(!string.IsNullOrEmpty(sel.InstalledVersion) ? sel.InstalledVersion : sel.ManifestValue)})" : "No")}");
        if (!string.IsNullOrEmpty(sel.ManifestValue)) sb.AppendLine($"Manifest:    {sel.ManifestValue}");
        _detailsField.value = sb.ToString();

        _openFolderBtn?.SetEnabled(Directory.Exists(sel.PathAbs));
        _installSingleBtn?.SetEnabled(!sel.Installed && Directory.Exists(sel.PathAbs));
        _uninstallBtn?.SetEnabled(sel.Installed && sel.InstalledFromLocal);
    }

    void OpenSelectedFolder()
    {
        if (_currentSelection == null) return;
        var pathAbs = _currentSelection.PathAbs;
        if (string.IsNullOrEmpty(pathAbs) || !Directory.Exists(pathAbs))
        {
            EditorUtility.DisplayDialog("Open Folder", "Folder does not exist.", "OK");
            return;
        }

#if UNITY_EDITOR_WIN
        System.Diagnostics.Process.Start("explorer.exe", $"\"{pathAbs}\"");
#elif UNITY_EDITOR_OSX
        System.Diagnostics.Process.Start("open", $"\"{pathAbs}\"");
#else
        EditorUtility.RevealInFinder(pathAbs);
#endif
    }

    void InstallSelectedSingle()
    {
        if (_currentSelection == null) return;
        var p = _currentSelection;
        if (p.Installed)
        {
            EditorUtility.DisplayDialog("Install", "Package is already installed.", "OK");
            return;
        }

        if (string.IsNullOrEmpty(p.PathRelForManifest) || !Directory.Exists(p.PathAbs))
        {
            EditorUtility.DisplayDialog("Install", "Package folder not found.", "OK");
            return;
        }

        var uri = $"file:{p.PathRelForManifest}"; // ОТНОСИТЕЛЬНО Packages/

        if (!EditorUtility.DisplayDialog("Install package",
                $"Add local package '{p.Name}'?\n{uri}", "Add", "Cancel"))
            return;

        var req = Client.Add(uri);
        _pendingAdds.Add(req);
        Debug.Log($"UPM Add → {p.Name} :: {uri}");

        EditorApplication.update -= UpdateAdds;
        EditorApplication.update += UpdateAdds;
    }

    void UninstallSelected()
    {
        if (_currentSelection == null) return;
        var p = _currentSelection;

        if (!p.Installed || !p.InstalledFromLocal)
        {
            EditorUtility.DisplayDialog("Uninstall", "Only locally installed (file:) packages can be uninstalled here.",
                "OK");
            return;
        }

        if (!EditorUtility.DisplayDialog("Uninstall package",
                $"Remove local package '{p.Name}' from manifest?", "Remove", "Cancel"))
            return;

        var req = Client.Remove(p.Name);
        _pendingRemoves.Add(req);
        Debug.Log($"UPM Remove → {p.Name}");

        EditorApplication.update -= UpdateRemoves;
        EditorApplication.update += UpdateRemoves;
    }

    // ===== Actions =====
    void ScanAndRefresh()
    {
        try
        {
            LoadManifestDependencies();
            _allFound = new List<PackageRow>();

            // нормализуем корни: из State (proj-relative) -> absolute
            var rootsAbs = State.instance.searchRoots
                .Select(ToAbsoluteFromProject)
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var rootAbs in rootsAbs)
            {
                foreach (var packageJsonPath in Directory.EnumerateFiles(rootAbs, "package.json",
                             SearchOption.AllDirectories))
                {
                    var norm = packageJsonPath.Replace('\\', '/');
                    if (norm.Contains("/node_modules/")) continue;

                    var pkgDirAbs = Path.GetDirectoryName(packageJsonPath)!;

                    try
                    {
                        var text = File.ReadAllText(packageJsonPath);
                        var jo = JObject.Parse(text);

                        var name = jo.Value<string>("name");
                        if (string.IsNullOrEmpty(name)) continue;

                        var row = new PackageRow
                        {
                            PathAbs = Path.GetFullPath(pkgDirAbs),
                            PathRelProj = ToProjectRelative(pkgDirAbs),
                            PathRelForManifest = ToPackagesRelative(pkgDirAbs), // КЛЮЧЕВОЕ местo
                            Name = name,
                            DisplayName = jo.Value<string>("displayName"),
                            Version = jo.Value<string>("version"),
                            Description = jo.Value<string>("description"),
                            Keywords = jo["keywords"]?.ToObject<List<string>>() ?? new List<string>()
                        };

                        if (_manifestDeps.TryGetValue(name, out var manifestValue))
                        {
                            row.Installed = true;
                            row.ManifestValue = manifestValue;
                            row.InstalledFromLocal = !string.IsNullOrEmpty(manifestValue) &&
                                                     manifestValue.StartsWith("file:",
                                                         StringComparison.OrdinalIgnoreCase);
                            if (!row.InstalledFromLocal)
                                row.InstalledVersion = manifestValue;
                        }

                        _allFound.Add(row);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Failed to read {packageJsonPath}: {ex.Message}");
                    }
                }
            }

            State.instance.lastSearchSummary = $"Found packages: {_allFound.Count}";
            State.instance.SaveNow();
            _summaryLabel.text = State.instance.lastSearchSummary;

            RefreshPackagesListView();
        }
        catch (Exception e)
        {
            Debug.LogError($"Scan error: {e}");
        }
    }

    void RefreshPackagesListView()
    {
        _packagesList.itemsSource = GetFiltered();
        _packagesList.Rebuild();
    }

    List<PackageRow> GetFiltered()
    {
        IEnumerable<PackageRow> q = _allFound;
        if (State.instance.showOnlyNotInstalled)
            q = q.Where(p => !p.Installed);
        return q.OrderBy(p => p.Installed).ThenBy(p => p.Name).ToList();
    }

    void LoadManifestDependencies()
    {
        _manifestDeps.Clear();
        var manifestPath = Path.GetFullPath(Path.Combine(PackagesRoot, "manifest.json"));
        if (!File.Exists(manifestPath)) return;

        var json = File.ReadAllText(manifestPath);
        var jo = JObject.Parse(json);
        var deps = jo["dependencies"] as JObject;
        if (deps == null) return;

        foreach (var prop in deps.Properties())
            _manifestDeps[prop.Name] = prop.Value?.ToString();
    }

    void InstallSelectedBatch()
    {
        var selected = State.instance.selectedPackageRelFromPackages.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toInstall = GetFiltered().Where(p => !p.Installed && selected.Contains(p.PathRelForManifest)).ToList();
        if (toInstall.Count == 0)
        {
            EditorUtility.DisplayDialog("UPM", "No selected packages to add.", "OK");
            return;
        }

        foreach (var p in toInstall)
        {
            if (string.IsNullOrEmpty(p.PathRelForManifest)) continue;
            var uri = $"file:{p.PathRelForManifest}"; // ОТНОСИТЕЛЬНО Packages/
            var req = Client.Add(uri);
            _pendingAdds.Add(req);
            Debug.Log($"UPM Add → {p.Name} :: {uri}");
        }

        EditorApplication.update -= UpdateAdds;
        EditorApplication.update += UpdateAdds;
    }

    void UpdateAdds()
    {
        for (int i = _pendingAdds.Count - 1; i >= 0; i--)
        {
            var r = _pendingAdds[i];
            if (!r.IsCompleted) continue;

            if (r.Status == StatusCode.Success)
                Debug.Log($"UPM: Added {r.Result?.name}@{r.Result?.version}");
            else
                Debug.LogError($"UPM: Add failed → {r.Error?.message}");

            _pendingAdds.RemoveAt(i);
        }

        if (_pendingAdds.Count == 0)
        {
            EditorApplication.update -= UpdateAdds;
            EditorApplication.delayCall += () =>
            {
                AssetDatabase.Refresh();
                ScanAndRefresh();
            };
        }
    }

    void UpdateRemoves()
    {
        for (int i = _pendingRemoves.Count - 1; i >= 0; i--)
        {
            var r = _pendingRemoves[i];
            if (!r.IsCompleted) continue;

            if (r.Status == StatusCode.Success)
                Debug.Log($"UPM: Removed {r.PackageIdOrName}");
            else
                Debug.LogError($"UPM: Remove failed → {r.Error?.message}");

            _pendingRemoves.RemoveAt(i);
        }

        if (_pendingRemoves.Count == 0)
        {
            EditorApplication.update -= UpdateRemoves;
            EditorApplication.delayCall += () =>
            {
                AssetDatabase.Refresh();
                ScanAndRefresh();
            };
        }
    }
}