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

public class UpmLocalPackagesWindow : EditorWindow
{
    [MenuItem("UniGame/Tools/UPM Local Packages")]
    public static void ShowWindow()
    {
        var wnd = GetWindow<UpmLocalPackagesWindow>();
        wnd.titleContent = new GUIContent("UPM Local Packages");
        wnd.minSize = new Vector2(980, 580);
        wnd.Show();
    }

    // ===== Persistent state =====
    [Serializable]
    [FilePath("ProjectSettings/UPMLocalPackagesState.json", FilePathAttribute.Location.ProjectFolder)]
    class State : ScriptableSingleton<State>
    {
        public List<string> searchRoots = new List<string>(); // project-relative (from project root)
        public bool showOnlyNotInstalled = false;
        public string lastSearchSummary = "";
        public List<string> selectedPackageRelFromPackages = new List<string>(); // relative to Packages/

        void OnEnable() => hideFlags |= HideFlags.DontSave;
        public void SaveNow() => Save(true);
    }

    // ===== Data model =====
    class PackageRow
    {
        public string PathAbs;
        public string PathRelProj;
        public string PathRelForManifest; // relative to Packages/ (used in file:)
        public string Name;
        public string DisplayName;
        public string Version;
        public string Description;
        public List<string> Keywords;

        public bool Installed; // direct in manifest
        public string InstalledVersion;
        public string ManifestValue;
        public bool InstalledFromLocal;

        public Dictionary<string, string> DeclaredDeps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    struct InstallAction
    {
        public enum Kind { Local, Git }
        public Kind Type;
        public PackageRow LocalPkg; // when Local
        public string DepName;      // name to display
        public string GitUrl;       // when Git
    }

    // UI refs
    private ListView _packagesList;
    private TextField _detailsField;
    private TextField _depsField;
    private Button _openFolderBtn;
    private Button _installSingleBtn;
    private Button _uninstallBtn;

    private ListView _pathsList;
    private Button _addPathBtn, _removePathBtn;

    private Button _scanBtn, _installBtn;
    private Toggle _onlyNotInstalledToggle;
    private Label _summaryLabel;

    private ToolbarToggle _tabDetails, _tabSettings;
    private VisualElement _panelDetails, _panelSettings;

    // Data
    private List<PackageRow> _allFound = new();
    private Dictionary<string, PackageRow> _foundByName = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _manifestDeps = new(); // name -> value
    private HashSet<string> _installedNames = new(StringComparer.OrdinalIgnoreCase); // manifest OR lock OR Client.List

    private HashSet<string> _highlightDepsByName = new(StringComparer.OrdinalIgnoreCase);

    // UPM requests
    private readonly List<AddRequest> _pendingAdds = new();
    private readonly List<RemoveRequest> _pendingRemoves = new();
    private ListRequest _listReq;

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
            var relUri = new Uri(baseDir.EndsWith(Path.DirectorySeparatorChar.ToString()) ? baseDir : baseDir + Path.DirectorySeparatorChar)
                .MakeRelativeUri(new Uri(target));
            return Uri.UnescapeDataString(relUri.ToString()).Replace('\\', '/');
        }
        catch
        {
            if (target.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                return target.Substring(baseDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
            return target.Replace('\\', '/');
        }
    }

    static string ToProjectRelative(string absPath) => ToRelativeFrom(ProjectRoot, absPath);
    static string ToPackagesRelative(string absPath) => ToRelativeFrom(PackagesRoot, absPath);

    void OnDisable() => State.instance.SaveNow();

    void CreateGUI()
    {
        var root = rootVisualElement;
        root.style.paddingLeft = 8;
        root.style.paddingRight = 8;
        root.style.paddingTop = 8;
        root.style.paddingBottom = 8;

        // ----- Top bar -----
        var topBar = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 6 } };
        root.Add(topBar);

        _scanBtn = new Button(ScanAndRefresh) { text = "Scan" };
        _scanBtn.style.marginRight = 8;
        topBar.Add(_scanBtn);

        _installBtn = new Button(InstallSelectedBatch) { text = "Update Packages (add selected + deps)" };
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

        // LEFT: packages list
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
            if (evt.newValue) { _tabSettings.SetValueWithoutNotify(false); ShowPanel(_panelDetails); }
            else if (!_tabSettings.value) { _tabDetails.SetValueWithoutNotify(true); }
        });
        _tabSettings.RegisterValueChangedCallback(evt =>
        {
            if (evt.newValue) { _tabDetails.SetValueWithoutNotify(false); ShowPanel(_panelSettings); }
            else if (!_tabDetails.value) { _tabSettings.SetValueWithoutNotify(true); }
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

        _depsField = new TextField("Dependencies") { multiline = true, isReadOnly = true };
        _depsField.style.flexGrow = 1;
        _depsField.style.whiteSpace = WhiteSpace.Normal;
        _depsField.style.marginTop = 6;
        _panelDetails.Add(_depsField);

        var detailsButtonsRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 6 } };

        _openFolderBtn = new Button(OpenSelectedFolder) { text = "Open Folder" };
        _openFolderBtn.style.marginRight = 6;
        _openFolderBtn.SetEnabled(false);
        detailsButtonsRow.Add(_openFolderBtn);

        _installSingleBtn = new Button(InstallSelectedSingle) { text = "Install (deps first)" };
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

        _pathsList = new ListView(State.instance.searchRoots, itemHeight: 20, makeItem: () => new Label(), bindItem: (ve, i) =>
        {
            (ve as Label).text = State.instance.searchRoots[i];
        })
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
        var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingLeft = 6, paddingRight = 6 } };

        var toggle = new Toggle { name = "sel" };
        toggle.style.width = 20;
        toggle.style.marginRight = 8;
        toggle.userData = null;
        row.Add(toggle);

        var name = new Label { name = "name" };
        name.style.minWidth = 260;
        name.style.marginRight = 8;
        row.Add(name);

        var ver = new Label { name = "ver" };
        ver.style.minWidth = 90;
        ver.style.marginRight = 8;
        row.Add(ver);

        var installed = new Label { name = "inst" };
        installed.style.minWidth = 220;
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

        var nameLbl = ve.Q<Label>("name");
        var baseName = string.IsNullOrEmpty(p.DisplayName) ? p.Name : p.DisplayName;
        nameLbl.text = _highlightDepsByName.Contains(p.Name) ? $"🔗 {baseName}" : baseName;

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
            if (_installedNames.Contains(p.Name))
            {
                inst.text = "Installed (registry/lock)";
                inst.style.color = new Color(0.5f, 1f, 0.5f);
            }
            else
            {
                inst.text = "Not installed";
                inst.style.color = new Color(1f, 0.6f, 0.3f);
            }
        }

        ve.Q<Label>("path").text = p.PathRelForManifest;

        if (_highlightDepsByName.Contains(p.Name))
            ve.style.backgroundColor = new Color(0.18f, 0.28f, 0.45f, 0.35f);
        else
            ve.style.backgroundColor = StyleKeyword.Null;
    }

    void OnSelectPackage(IEnumerable<object> objs)
    {
        var sel = objs?.FirstOrDefault() as PackageRow;
        _currentSelection = sel;
        _highlightDepsByName.Clear();

        if (sel == null)
        {
            _detailsField.value = "";
            _depsField.value = "";
            _openFolderBtn?.SetEnabled(false);
            _installSingleBtn?.SetEnabled(false);
            _uninstallBtn?.SetEnabled(false);
            _packagesList.Rebuild();
            return;
        }

        foreach (var dep in sel.DeclaredDeps.Keys)
            _highlightDepsByName.Add(dep);

        var sb = new StringBuilder();
        sb.AppendLine($"Name:        {sel.Name}");
        if (!string.IsNullOrEmpty(sel.DisplayName)) sb.AppendLine($"DisplayName: {sel.DisplayName}");
        sb.AppendLine($"Version:     {sel.Version}");
        if (!string.IsNullOrEmpty(sel.Description)) sb.AppendLine($"Description: {sel.Description}");
        if (sel.Keywords != null && sel.Keywords.Count > 0) sb.AppendLine($"Tags:        {string.Join(", ", sel.Keywords)}");
        sb.AppendLine($"Path (abs):  {sel.PathAbs}");
        sb.AppendLine($"Path (proj): {sel.PathRelProj}");
        sb.AppendLine($"Path (for manifest): {sel.PathRelForManifest}");
        sb.AppendLine($"Installed:   {(sel.Installed ? $"Yes ({(sel.InstalledFromLocal ? "local" : "registry")}: {(!string.IsNullOrEmpty(sel.InstalledVersion) ? sel.InstalledVersion : sel.ManifestValue)})" : (_installedNames.Contains(sel.Name) ? "Yes (registry/lock)" : "No"))}");
        if (!string.IsNullOrEmpty(sel.ManifestValue)) sb.AppendLine($"Manifest:    {sel.ManifestValue}");
        _detailsField.value = sb.ToString();

        _depsField.value = BuildDependenciesText(sel);

        _openFolderBtn?.SetEnabled(Directory.Exists(sel.PathAbs));
        _installSingleBtn?.SetEnabled(!sel.Installed && Directory.Exists(sel.PathAbs) && !_installedNames.Contains(sel.Name));
        _uninstallBtn?.SetEnabled(sel.Installed && sel.InstalledFromLocal);

        _packagesList.Rebuild();
    }

    string BuildDependenciesText(PackageRow pkg)
    {
        if (pkg.DeclaredDeps == null || pkg.DeclaredDeps.Count == 0)
            return "(no dependencies)";

        var lines = new List<string>();
        foreach (var kv in pkg.DeclaredDeps.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var depName = kv.Key.Trim();
            var spec    = (kv.Value ?? "").Trim();
            var installed = _installedNames.Contains(depName);
            var local = _foundByName.ContainsKey(depName);
            var isGit = IsGitSpecifier(spec);
            var status = new List<string>();
            if (installed) status.Add("installed");
            if (local) status.Add("local");
            if (isGit) status.Add("git");
            if (!installed && (local || isGit)) status.Add("auto-install");
            if (status.Count == 0) status.Add("missing");
            lines.Add($"{depName} : {spec}  [{string.Join(", ", status)}]");
        }

        var plan = BuildInstallPlan(new[] { pkg });
        var summary = plan.Select(a => a.Type == InstallAction.Kind.Local
            ? $"{a.LocalPkg.Name} (file:{a.LocalPkg.PathRelForManifest})"
            : $"{a.DepName} (git:{a.GitUrl})");
        lines.Add("");
        lines.Add("Install order:");
        lines.AddRange(summary.Select(s => "  • " + s));

        return string.Join("\n", lines);
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

    // ===== Install / Uninstall =====

    void InstallSelectedSingle()
    {
        if (_currentSelection == null) return;
        var p = _currentSelection;
        if (p.Installed || _installedNames.Contains(p.Name))
        {
            EditorUtility.DisplayDialog("Install", "Package is already installed.", "OK");
            return;
        }
        if (string.IsNullOrEmpty(p.PathRelForManifest) || !Directory.Exists(p.PathAbs))
        {
            EditorUtility.DisplayDialog("Install", "Package folder not found.", "OK");
            return;
        }

        var plan = BuildInstallPlan(new[] { p });
        if (plan.Count == 0)
        {
            EditorUtility.DisplayDialog("Install", "Nothing to install.", "OK");
            return;
        }

        var msg = string.Join("\n", plan.Select(a =>
            a.Type == InstallAction.Kind.Local
                ? $" • {a.LocalPkg.Name}  (file:{a.LocalPkg.PathRelForManifest})"
                : $" • {a.DepName}  (git:{a.GitUrl})"));

        if (!EditorUtility.DisplayDialog("Install (with dependencies)",
            $"The following will be installed in order:\n{msg}", "Install", "Cancel"))
            return;

        EnqueueAddRequests(plan);
    }

    void InstallSelectedBatch()
    {
        var selected = State.instance.selectedPackageRelFromPackages.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = GetFiltered().Where(p => !p.Installed && !_installedNames.Contains(p.Name) && selected.Contains(p.PathRelForManifest)).ToList();
        if (roots.Count == 0)
        {
            EditorUtility.DisplayDialog("UPM", "No selected packages to add.", "OK");
            return;
        }

        var plan = BuildInstallPlan(roots);
        var msg = string.Join("\n", plan.Select(a =>
            a.Type == InstallAction.Kind.Local
                ? $" • {a.LocalPkg.Name}  (file:{a.LocalPkg.PathRelForManifest})"
                : $" • {a.DepName}  (git:{a.GitUrl})"));

        if (!EditorUtility.DisplayDialog("Install (with dependencies)",
            $"The following will be installed in order:\n{msg}", "Install", "Cancel"))
            return;

        EnqueueAddRequests(plan);
    }

    void UninstallSelected()
    {
        if (_currentSelection == null) return;
        var p = _currentSelection;

        if (!p.Installed || !p.InstalledFromLocal)
        {
            EditorUtility.DisplayDialog("Uninstall", "Only locally installed (file:) packages can be uninstalled here.", "OK");
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

    // ===== Planning =====

    static bool IsGitSpecifier(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();
        if (s.StartsWith("git+", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("git://", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return s.Contains(".git", StringComparison.OrdinalIgnoreCase) || s.Contains("github.com", StringComparison.OrdinalIgnoreCase) || s.Contains("gitlab", StringComparison.OrdinalIgnoreCase) || s.Contains("bitbucket", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    List<InstallAction> BuildInstallPlan(IEnumerable<PackageRow> roots)
    {
        var actions = new List<InstallAction>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var plannedGitByName   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plannedLocalByName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool IsInstalledAny(string name) => _installedNames.Contains(name);

        void DfsNode(PackageRow node)
        {
            if (node == null) return;
            if (visited.Contains(node.Name)) return;
            if (visiting.Contains(node.Name)) return;
            visiting.Add(node.Name);

            foreach (var kv in node.DeclaredDeps)
            {
                var depName = kv.Key.Trim();
                var spec    = (kv.Value ?? "").Trim();

                if (_foundByName.TryGetValue(depName, out var depLocal))
                {
                    DfsNode(depLocal);
                }
                else if (IsGitSpecifier(spec) && !IsInstalledAny(depName) && !plannedGitByName.Contains(depName))
                {
                    actions.Add(new InstallAction { Type = InstallAction.Kind.Git, DepName = depName, GitUrl = spec });
                    plannedGitByName.Add(depName);
                }
            }

            visiting.Remove(node.Name);
            visited.Add(node.Name);

            if (!IsInstalledAny(node.Name) && !plannedLocalByName.Contains(node.Name))
            {
                actions.Add(new InstallAction { Type = InstallAction.Kind.Local, LocalPkg = node, DepName = node.Name });
                plannedLocalByName.Add(node.Name);
            }
        }

        foreach (var r in roots) DfsNode(r);
        return actions;
    }

    void EnqueueAddRequests(List<InstallAction> plan)
    {
        if (plan.Count == 0)
        {
            EditorUtility.DisplayDialog("UPM", "Nothing to install.", "OK");
            return;
        }

        foreach (var a in plan)
        {
            if (a.Type == InstallAction.Kind.Local)
            {
                var uri = $"file:{a.LocalPkg.PathRelForManifest}";
                var req = Client.Add(uri);
                _pendingAdds.Add(req);
                Debug.Log($"UPM Add (local) → {a.LocalPkg.Name} :: {uri}");
            }
            else
            {
                var req = Client.Add(a.GitUrl);
                _pendingAdds.Add(req);
                Debug.Log($"UPM Add (git)   → {a.DepName} :: {a.GitUrl}");
            }
        }

        EditorApplication.update -= UpdateAdds;
        EditorApplication.update += UpdateAdds;
    }

    // ===== Scan / Refresh =====
    void ScanAndRefresh()
    {
        try
        {
            LoadManifestDependencies(); // also kicks off Client.List(true)

            _allFound.Clear();
            _foundByName.Clear();

            var rootsAbs = State.instance.searchRoots
                .Select(ToAbsoluteFromProject)
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var rootAbs in rootsAbs)
            {
                foreach (var packageJsonPath in Directory.EnumerateFiles(rootAbs, "package.json", SearchOption.AllDirectories))
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
                            PathRelForManifest = ToPackagesRelative(pkgDirAbs),
                            Name = name,
                            DisplayName = jo.Value<string>("displayName"),
                            Version = jo.Value<string>("version"),
                            Description = jo.Value<string>("description"),
                            Keywords = jo["keywords"]?.ToObject<List<string>>() ?? new List<string>(),
                            DeclaredDeps = ParseDependencies(jo["dependencies"] as JObject)
                        };

                        if (_manifestDeps.TryGetValue(name, out var manifestValue))
                        {
                            row.Installed = true;
                            row.ManifestValue = manifestValue;
                            row.InstalledFromLocal = !string.IsNullOrEmpty(manifestValue) &&
                                                     manifestValue.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
                            if (!row.InstalledFromLocal)
                                row.InstalledVersion = manifestValue;
                        }

                        _allFound.Add(row);
                        _foundByName[name] = row;
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

            if (_currentSelection != null)
            {
                _depsField.value = BuildDependenciesText(_currentSelection);
                _packagesList.Rebuild();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Scan error: {e}");
        }
    }

    static Dictionary<string, string> ParseDependencies(JObject deps)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (deps == null) return dict;
        foreach (var p in deps.Properties())
            dict[p.Name.Trim()] = p.Value?.ToString()?.Trim() ?? "";
        return dict;
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
            q = q.Where(p => !p.Installed && !_installedNames.Contains(p.Name));
        return q.OrderBy(p => p.Installed || _installedNames.Contains(p.Name))
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    // ===== Installed detection =====
    void LoadManifestDependencies()
    {
        _manifestDeps.Clear();
        _installedNames.Clear();

        var manifestPath = Path.GetFullPath(Path.Combine(PackagesRoot, "manifest.json"));
        if (File.Exists(manifestPath))
        {
            try
            {
                var json = File.ReadAllText(manifestPath);
                var jo = JObject.Parse(json);
                var deps = jo["dependencies"] as JObject;
                if (deps != null)
                {
                    foreach (var prop in deps.Properties())
                    {
                        var name = prop.Name?.Trim();
                        var val = prop.Value?.ToString();
                        if (string.IsNullOrEmpty(name)) continue;
                        _manifestDeps[name] = val;
                        _installedNames.Add(name);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to read manifest.json: {e.Message}");
            }
        }

        var lockPath = Path.GetFullPath(Path.Combine(PackagesRoot, "packages-lock.json"));
        if (File.Exists(lockPath))
        {
            try
            {
                var json = File.ReadAllText(lockPath);
                var jo = JObject.Parse(json);
                var deps = jo["dependencies"] as JObject;
                if (deps != null)
                {
                    foreach (var prop in deps.Properties())
                    {
                        var name = prop.Name?.Trim();
                        if (!string.IsNullOrEmpty(name))
                            _installedNames.Add(name);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to read packages-lock.json: {e.Message}");
            }
        }

        // Also ask UPM for the actually installed list
        EditorApplication.update -= PumpListInstalled;
        _listReq = Client.List(true);
        EditorApplication.update += PumpListInstalled;
    }

    void PumpListInstalled()
    {
        if (_listReq == null || !_listReq.IsCompleted) return;

        EditorApplication.update -= PumpListInstalled;

        if (_listReq.Status == StatusCode.Success && _listReq.Result != null)
        {
            foreach (var p in _listReq.Result)
            {
                if (!string.IsNullOrEmpty(p.name))
                    _installedNames.Add(p.name.Trim());
            }
        }
        else if (_listReq.Status >= StatusCode.Failure)
        {
            Debug.LogWarning($"UPM List failed: {_listReq.Error?.message}");
        }

        _listReq = null;

        RefreshPackagesListView();
        if (_currentSelection != null)
            _depsField.value = BuildDependenciesText(_currentSelection);
    }

    // ===== UPM pumps =====
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
