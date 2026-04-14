// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
#if NET8_0_OR_GREATER
using System.Runtime.Loader;
#endif

namespace Bibim.Core
{
    /// <summary>
    /// BIBIM — Revit Native C# Add-in entry point.
    /// Implements IExternalApplication for full Revit lifecycle access.
    /// Version is read from assembly (set in csproj) — single source of truth.
    /// </summary>
    public class BibimApp : IExternalApplication
    {
        internal static string AppVersion { get; }
#if APP_LANG_EN
            = (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0") + "-en";
#else
            = (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0") + "-kr";
#endif

        internal static string AppBuildInfo { get; }
            = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? AppVersion;

        /// <summary>
        /// Runtime-detected Revit version (e.g., "2025", "2026").
        /// Set on first Idling event from app.Application.VersionNumber.
        /// Falls back to rag_config.json if not yet captured.
        /// </summary>
        internal static string DetectedRevitVersion { get; private set; }

        /// <summary>
        /// Cached result of the startup version check. Null if no update needed or not yet checked.
        /// </summary>
        internal static VersionCheckResult LastVersionCheckResult { get; set; }

        internal static BibimApp Instance { get; private set; }
        internal static UIControlledApplication UiCtrlApp { get; private set; }
        internal static UIApplication CurrentUiApp { get; private set; }

        internal static ExternalEvent ExecutionEvent { get; private set; }
        internal static BibimExecutionHandler ExecutionHandler { get; private set; }

#if NET8_0_OR_GREATER
        private static AssemblyDependencyResolver _dependencyResolver;
        private static AssemblyLoadContext _loadContext;
#endif
        private static bool _assemblyResolverInitialized;

        // Valid hex GUID for dockable pane
        private static readonly Guid DockablePaneGuid
            = new Guid("B1B1B1B1-B1B1-B1B1-B1B1-B1B1D0C0AB01");

        public Result OnStartup(UIControlledApplication application)
        {
            Instance = this;
            UiCtrlApp = application;
            InitializeAssemblyResolver();

            try
            {
                Logger.Log("BibimApp", $"BIBIM v{AppVersion} OnStartup begin (build={AppBuildInfo})");

                // 1. Ribbon UI FIRST — this must succeed for the tab to appear
                CreateRibbonUI(application);
                Logger.Log("BibimApp", "Ribbon UI created");

                // 2. ExternalEvent handler
                ExecutionHandler = new BibimExecutionHandler();
                ExecutionEvent = ExternalEvent.Create(ExecutionHandler);
                Logger.Log("BibimApp", "ExternalEvent created");

                // 3. Infrastructure init — wrapped separately so ribbon survives
                try
                {
                    AppLanguage.Initialize();
                    LocalizationService.Initialize(AppLanguage.Current);
                    ServiceContainer.Initialize();

                    // Register core services in DI container
                    ServiceContainer.Register(new RevitContextProvider());
                    ServiceContainer.Register(new RoslynAnalyzerService());
                    ServiceContainer.Register(new RoslynCompilerService());

                    Logger.Log("BibimApp", "Services initialized and registered");
                }
                catch (Exception svcEx)
                {
                    Logger.Log("BibimApp", $"Service init partial fail (non-fatal): {svcEx.Message}");
                }

                // 4. Hook into Revit Idling to capture UIApplication for RevitContextProvider
                // UIControlledApplication doesn't expose UIApplication directly,
                // so we grab it on the first Idling event.
                application.Idling += OnFirstIdling;

                // 4. Dockable pane — register after ribbon
                try
                {
                    var panelId = new DockablePaneId(DockablePaneGuid);
                    application.RegisterDockablePane(panelId, "BIBIM AI",
                        new BibimDockablePanelProvider());
                    Logger.Log("BibimApp", "Dockable pane registered");
                }
                catch (Exception dockEx)
                {
                    Logger.Log("BibimApp",
                        $"Dockable pane registration deferred: {dockEx.Message}");
                }

                Logger.Log("BibimApp", $"BIBIM v{AppVersion} started successfully");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.LogError("BibimApp", ex);
                return Result.Succeeded;
            }
        }

        /// <summary>
        /// Capture UIApplication on first Idling event and inject into RevitContextProvider.
        /// UIControlledApplication (from OnStartup) doesn't expose UIApplication,
        /// so we use the Idling event which provides it via sender.
        /// </summary>
        private void OnFirstIdling(object sender, IdlingEventArgs e)
        {
            try
            {
                // Unsubscribe immediately — we only need this once
                UiCtrlApp.Idling -= OnFirstIdling;

                var uiApp = sender as UIApplication;
                if (uiApp != null)
                {
                    CurrentUiApp = uiApp;

                    // Capture runtime Revit version (e.g., "2025")
                    try
                    {
                        DetectedRevitVersion = uiApp.Application.VersionNumber;
                        Logger.Log("BibimApp", $"Runtime Revit version: {DetectedRevitVersion}");
                    }
                    catch (Exception verEx)
                    {
                        Logger.Log("BibimApp", $"Version detection failed (non-fatal): {verEx.Message}");
                    }

                    try
                    {
                        uiApp.Application.DocumentChanged += OnRevitDocumentChanged;
                    }
                    catch (Exception docChangedEx)
                    {
                        Logger.Log("BibimApp",
                            $"DocumentChanged hook skipped (non-fatal): {docChangedEx.Message}");
                    }

                    var contextProvider = ServiceContainer.GetService<RevitContextProvider>();
                    if (contextProvider != null)
                    {
                        contextProvider.SetApplication(uiApp);
                        Logger.Log("BibimApp", "RevitContextProvider received UIApplication");
                    }

                    // Fire-and-forget version check
                    Task.Run(async () =>
                    {
                        try
                        {
                            var result = await VersionChecker.CheckForUpdatesAsync();
                            if (result.UpdateRequired)
                            {
                                LastVersionCheckResult = result;
                                Logger.Log("BibimApp",
                                    $"Update available: {result.LatestVersion} (mandatory={result.IsMandatory})");
                            }
                        }
                        catch (Exception vcEx)
                        {
                            Logger.Log("BibimApp", $"Version check failed (non-fatal): {vcEx.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Log("BibimApp", $"OnFirstIdling error (non-fatal): {ex.Message}");
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            Logger.Log("BibimApp", $"BIBIM v{AppVersion} shutting down");
            try
            {
                if (CurrentUiApp != null)
                    CurrentUiApp.Application.DocumentChanged -= OnRevitDocumentChanged;
            }
            catch
            {
            }

            ServiceContainer.Reset();
            WindowsNotificationService.Dispose();
            return Result.Succeeded;
        }

        private void OnRevitDocumentChanged(object sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs e)
        {
            try
            {
                DocumentChangeTracker.RegisterChange(e.GetDocument());
            }
            catch (Exception ex)
            {
                Logger.Log("BibimApp", $"DocumentChanged tracker skipped: {ex.Message}");
            }
        }

        private void CreateRibbonUI(UIControlledApplication app)
        {
            string tabName = "BIBIM AI";

            try
            {
                app.CreateRibbonTab(tabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // Tab already exists (e.g., from a previous failed load)
                Logger.Log("BibimApp", "Ribbon tab already exists, reusing");
            }

            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            Logger.Log("BibimApp", $"Assembly: {assemblyPath}");
            Logger.Log("BibimApp", $"Build info: {AppBuildInfo}");
            Logger.Log("BibimApp", $"Assembly timestamp: {File.GetLastWriteTime(assemblyPath):yyyy-MM-dd HH:mm:ss}");

            var panel = app.CreateRibbonPanel(tabName, "BIBIM");

            string btnText = "BIBIM AI";
            try { btnText = LocalizationService.Get("Extension_OpenChatMenu"); }
            catch { /* fallback to default */ }

            var showPanelBtn = new PushButtonData(
                "BibimShowPanel",
                btnText,
                assemblyPath,
                typeof(BibimShowPanelCommand).FullName);

            // Icon setup — non-critical
            try
            {
                string iconDir = Path.Combine(
                    Path.GetDirectoryName(assemblyPath) ?? "",
                    "Assets", "Icons");

                string icon32 = Path.Combine(iconDir, "bibim-icon-32.png");
                string icon16 = Path.Combine(iconDir, "bibim-icon-16.png");

                if (File.Exists(icon32))
                    showPanelBtn.LargeImage = new BitmapImage(new Uri(icon32));
                if (File.Exists(icon16))
                    showPanelBtn.Image = new BitmapImage(new Uri(icon16));

                Logger.Log("BibimApp", $"Icons: 32={File.Exists(icon32)}, 16={File.Exists(icon16)}");
            }
            catch (Exception iconEx)
            {
                Logger.Log("BibimApp", $"Icon load skipped: {iconEx.Message}");
            }

            showPanelBtn.ToolTip = "Open BIBIM AI Assistant";
            panel.AddItem(showPanelBtn);

            Logger.Log("BibimApp", "Ribbon UI created");
        }

        private static void InitializeAssemblyResolver()
        {
            if (_assemblyResolverInitialized)
                return;

            _assemblyResolverInitialized = true;

            var entryAssembly = Assembly.GetExecutingAssembly();
            var addinDir = Path.GetDirectoryName(entryAssembly.Location) ?? AppContext.BaseDirectory;

            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

#if NET8_0_OR_GREATER
            try
            {
                _loadContext = AssemblyLoadContext.GetLoadContext(entryAssembly) ?? AssemblyLoadContext.Default;
                _dependencyResolver = new AssemblyDependencyResolver(entryAssembly.Location);
                _loadContext.Resolving += OnLoadContextResolving;

                Logger.Log("BibimApp", $"Assembly resolver installed: context={_loadContext.Name ?? "Default"} dir={addinDir}");

                // Revit 2025/2026 add-in host does not always honor plugin deps.json
                // for transitive package assemblies. Preload the known hot path now.
                PreloadManagedAssembly("System.Text.Json");
                PreloadManagedAssembly("System.Text.Encodings.Web");
                PreloadManagedAssembly("Anthropic");
            }
            catch (Exception ex)
            {
                Logger.Log("BibimApp", $"AssemblyLoadContext resolver init failed: {ex.Message}");
            }
#else
            Logger.Log("BibimApp", $"Assembly resolver installed: AppDomain dir={addinDir}");
#endif
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            return TryResolveAssembly(new AssemblyName(args.Name), preferLoadContext: false);
        }

#if NET8_0_OR_GREATER
        private static Assembly OnLoadContextResolving(AssemblyLoadContext context, AssemblyName assemblyName)
        {
            return TryResolveAssembly(assemblyName, preferLoadContext: true);
        }
#endif

        private static Assembly TryResolveAssembly(AssemblyName assemblyName, bool preferLoadContext)
        {
            try
            {
                if (assemblyName == null || string.IsNullOrWhiteSpace(assemblyName.Name))
                    return null;

                if (assemblyName.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                    return null;

                var loaded = FindLoadedAssembly(assemblyName);
                if (loaded != null)
                    return loaded;

                string resolvedPath = ResolveManagedAssemblyPath(assemblyName);
                if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                    return null;

                Logger.Log("BibimApp", $"Resolving assembly: {assemblyName.Name} -> {resolvedPath}");

#if NET8_0_OR_GREATER
                if (preferLoadContext && _loadContext != null)
                    return _loadContext.LoadFromAssemblyPath(resolvedPath);
#endif
                return Assembly.LoadFrom(resolvedPath);
            }
            catch (Exception ex)
            {
                Logger.Log("BibimApp", $"Assembly resolve failed for {assemblyName?.Name}: {ex.Message}");
                return null;
            }
        }

        private static string ResolveManagedAssemblyPath(AssemblyName assemblyName)
        {
            var addinDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;

#if NET8_0_OR_GREATER
            try
            {
                string depsPath = _dependencyResolver?.ResolveAssemblyToPath(assemblyName);
                if (!string.IsNullOrWhiteSpace(depsPath) && File.Exists(depsPath))
                    return depsPath;
            }
            catch (Exception ex)
            {
                Logger.Log("BibimApp", $"Dependency resolver lookup failed for {assemblyName.Name}: {ex.Message}");
            }
#endif

            string localDll = Path.Combine(addinDir, assemblyName.Name + ".dll");
            return File.Exists(localDll) ? localDll : null;
        }

        private static Assembly FindLoadedAssembly(AssemblyName assemblyName)
        {
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var loadedName = loaded.GetName();
                    if (AssemblyName.ReferenceMatchesDefinition(loadedName, assemblyName))
                        return loaded;

                    if (string.Equals(loadedName.Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
                        return loaded;
                }
                catch
                {
                    // Ignore and continue scanning.
                }
            }

            return null;
        }

        private static void PreloadManagedAssembly(string simpleName)
        {
            try
            {
                var assemblyName = new AssemblyName(simpleName);
                var loaded = FindLoadedAssembly(assemblyName);
                if (loaded != null)
                {
                    Logger.Log("BibimApp", $"Assembly already loaded: {loaded.FullName}");
                    return;
                }

                string resolvedPath = ResolveManagedAssemblyPath(assemblyName);
                if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                {
                    Logger.Log("BibimApp", $"Preload skipped (not found): {simpleName}");
                    return;
                }

#if NET8_0_OR_GREATER
                var context = _loadContext ?? AssemblyLoadContext.Default;
                var loadedAssembly = context.LoadFromAssemblyPath(resolvedPath);
#else
                var loadedAssembly = Assembly.LoadFrom(resolvedPath);
#endif
                Logger.Log("BibimApp", $"Preloaded assembly: {loadedAssembly.FullName}");
            }
            catch (Exception ex)
            {
                Logger.Log("BibimApp", $"Preload failed for {simpleName}: {ex.Message}");
            }
        }
    }
}
