using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using Siemens.Engineering;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Communication;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Hmi.Tag;
using Siemens.Engineering.Hmi.TextGraphicList;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.HmiConnections;
using Siemens.Engineering.HmiUnified.UI.Screens;
using Siemens.Engineering.HmiUnified.UI.ScreenGroup;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;
using Siemens.Engineering.MC.Drives;
using Siemens.Engineering.MC.Drives.DFI;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Library;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.Library.Types;
using System.Xml.Linq;
using System.Runtime.CompilerServices;
using static TiaApiServer.TiaBootstrap;

namespace TiaApiServer
{
    /// <summary>
    /// AssemblyResolve 와 TIA 버전 선택.
    ///
    /// Program 에 두면 안 된다 — Program 은 TiaPortal/UserGlobalLibrary 같은
    /// Openness 타입의 static 필드를 갖고 있어서, 그 클래스에 처음 접근하는
    /// 순간 필드 초기화가 리졸버 등록보다 **먼저** 돌아 V20 을 찾다가 죽는다
    /// (실측: TypeInitializationException -> FileNotFoundException).
    /// 그래서 Openness 를 일절 참조하지 않는 이 클래스가 먼저 리졸버를 건다.
    /// </summary>
    internal static class TiaBootstrap
    {

        // ── TIA 버전 선택 ──────────────────────────────────────
        //
        // .NET 은 한 프로세스에 같은 이름의 어셈블리를 한 번만 로드하고 교체할
        // 수 없다. 그래서 대상 버전은 **기동 시 한 번** 정해지고, 로드된 뒤에는
        // 바꿀 수 없다 — 바꾸려면 프로세스를 다시 띄워야 한다.
        //
        //   TiaApiServer.exe --tia=19          (또는 환경변수 TIA_VERSION=19)
        //
        // V19/V20 은 PublicAPI\V{n}\Siemens.Engineering.dll 로 어셈블리 이름이
        // 같아서 SpecificVersion=False + AssemblyResolve 로 서로 갈아끼울 수 있다.
        //
        // V21 은 그 단일 어셈블리가 Base / Step7 / WinCC / WinCCUnified /
        // Startdrive 로 쪼개지고, 경로도 PublicAPI\V21\net48 로 한 단계 깊어진다.
        // 어셈블리 '이름' 이 달라지므로 한 바이너리가 V20 과 V21 을 겸할 수 없다
        // — 그래서 빌드를 버전별로 나눈다 (TiaApiServer.V{19,20,21}.csproj).
        // 네임스페이스와 타입 이름은 전 버전 동일하므로 이 파일은 그대로 공유한다.
#if TIA_V21
        public static readonly int[] SupportedMajors = { 21 };
#else
        public static readonly int[] SupportedMajors = { 19, 20 };
#endif
        public static int selectedMajor;
        public static string versionSource = "default";
        public static int port = 8099;

        public static string PortalDir(int major)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Siemens\Automation\Portal V" + major);
        }

        public static string PublicApiDir(int major)
        {
            string baseDir = Path.Combine(PortalDir(major), "PublicAPI", "V" + major);
            // V21 부터 net48 타깃 폴더가 한 단계 더 있다 (지원은 안 하지만
            // 상태 조회에서 올바른 경로를 보여주기 위해 규칙은 반영한다)
            return major >= 21 ? Path.Combine(baseDir, "net48") : baseDir;
        }

        public static string BinPublicApiDir(int major)
        {
            return Path.Combine(PortalDir(major), "Bin", "PublicAPI");
        }

        public static bool IsVersionInstalled(int major)
        {
            string core = major >= 21 ? "Siemens.Engineering.Base.dll" : "Siemens.Engineering.dll";
            return File.Exists(Path.Combine(PublicApiDir(major), core));
        }

        public static List<int> InstalledMajors()
        {
            var found = new List<int>();
            foreach (int m in new[] { 19, 20, 21 })
                if (IsVersionInstalled(m)) found.Add(m);
            return found;
        }

        /// <summary>Openness 를 로드하지 않고 실행 중인 TIA 의 메이저 버전을 본다.</summary>
        public static List<int> RunningPortalMajors()
        {
            var found = new List<int>();
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("Siemens.Automation.Portal"))
                {
                    string path = null;
                    try { path = p.MainModule != null ? p.MainModule.FileName : null; }
                    catch { }
                    if (path == null) continue;
                    var m = System.Text.RegularExpressions.Regex.Match(
                        path, @"Portal V(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!m.Success) continue;
                    int major = int.Parse(m.Groups[1].Value);
                    if (!found.Contains(major)) found.Add(major);
                }
            }
            catch { }
            return found;
        }

        /// <summary>실제로 AppDomain 에 올라온 Openness 의 메이저 (없으면 null).</summary>
        public static int? LoadedMajor()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try { name = asm.GetName().Name; }
                catch { continue; }
                if (name != "Siemens.Engineering" && name != "Siemens.Engineering.Base") continue;
                try { return asm.GetName().Version.Major; }
                catch { return null; }
            }
            return null;
        }

        public static int ResolveRequestedMajor()
        {
            // 1) 커맨드라인 --tia=19 / /tia:19
            try
            {
                foreach (string arg in Environment.GetCommandLineArgs())
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        arg, @"^[-/]{1,2}tia[=:]v?(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        versionSource = "commandline";
                        return int.Parse(m.Groups[1].Value);
                    }
                }
            }
            catch { }

            // 2) 환경변수
            try
            {
                string env = Environment.GetEnvironmentVariable("TIA_VERSION");
                if (!string.IsNullOrEmpty(env))
                {
                    int parsed;
                    if (int.TryParse(env.Trim().TrimStart('v', 'V').Split('.')[0], out parsed))
                    {
                        versionSource = "env:TIA_VERSION";
                        return parsed;
                    }
                }
            }
            catch { }

            // 3) 자동: 떠 있는 TIA 의 버전을 따른다 (지원 목록 안에서)
            foreach (int major in RunningPortalMajors())
            {
                if (Array.IndexOf(SupportedMajors, major) >= 0 && IsVersionInstalled(major))
                {
                    versionSource = "auto:running";
                    return major;
                }
            }

            // 4) 설치된 것 중 가장 높은 지원 버전
            var installed = InstalledMajors();
            for (int i = SupportedMajors.Length - 1; i >= 0; i--)
                if (installed.Contains(SupportedMajors[i]))
                {
                    versionSource = "auto:installed";
                    return SupportedMajors[i];
                }

            versionSource = "fallback";
            return 20;
        }

        /// <summary>Openness 타입을 건드리기 전에 반드시 먼저 호출할 것.</summary>

        static readonly JavaScriptSerializer bootJson =
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static void SendJson(HttpListenerResponse resp, int status, object data)
        {
            resp.StatusCode = status;
            resp.ContentType = "application/json; charset=utf-8";
            byte[] buf = Encoding.UTF8.GetBytes(bootJson.Serialize(data));
            resp.ContentLength64 = buf.Length;
            resp.OutputStream.Write(buf, 0, buf.Length);
            resp.Close();
        }

        /// <summary>
        /// 버전 관련 엔드포인트는 여기서 먼저 처리한다. Program 으로 넘기면
        /// 그 클래스의 static 필드(TiaPortal 등) 때문에 Openness 가 로드돼
        /// 버려서, 정작 전환이 필요한 시점에 항상 늦는다.
        /// </summary>
        public static bool TryHandle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
            string method = ctx.Request.HttpMethod;

            if (path == "/api/dll/versions") { GetTiaVersions(ctx.Response); return true; }
            if (path == "/api/dll/switch" && method == "PUT") { SwitchTiaVersion(ctx); return true; }
            return false;
        }

        public static void ParsePort(string[] args)
        {
            // 포트는 "숫자만인 인자" 로 받는다 — 옵션과 섞여도 순서 무관.
            foreach (string arg in args)
            {
                int parsed;
                if (int.TryParse(arg, out parsed) && parsed > 0 && parsed < 65536) { port = parsed; return; }
            }
        }

        public static void PrintBanner(string prefix)
        {
            Console.WriteLine("===========================================");
            Console.WriteLine("  TIA Portal Openness API Server");
            Console.WriteLine("  Running at: " + prefix);
            Console.WriteLine("  Open in browser: " + prefix);
            Console.WriteLine("  TIA target : V" + selectedMajor + "  (" + versionSource + ")");
            Console.WriteLine("               " + PublicApiDir(selectedMajor));
            if (!IsVersionInstalled(selectedMajor))
                Console.WriteLine("  !! 경고: 해당 경로에 Openness 어셈블리가 없습니다");
            Console.WriteLine("  버전 변경 : TiaApiServer.exe " + port + " --tia=19");
            Console.WriteLine("              (기동 직후라면 PUT /api/dll/switch?version=19 도 가능)");
            Console.WriteLine("  Press Ctrl+C to stop");
            Console.WriteLine("===========================================");
        }

        // GET /api/dll/versions
        // 설치된 TIA 버전 · 현재 대상 버전 · 실제 로드된 버전 · 실행 중인 포탈 버전
        static void GetTiaVersions(HttpListenerResponse resp)
        {
            var installed = InstalledMajors();
            var running = RunningPortalMajors();
            int? loaded = LoadedMajor();

            var list = new List<object>();
            foreach (int major in new[] { 19, 20, 21 })
            {
                bool supported = Array.IndexOf(SupportedMajors, major) >= 0;
                list.Add(new {
                    version = "V" + major,
                    major,
                    installed = installed.Contains(major),
                    running = running.Contains(major),
                    active = major == selectedMajor,
                    loaded = loaded.HasValue && loaded.Value == major,
                    supported,
                    note = supported
                        ? null
                        : "이 바이너리가 참조하는 Openness 어셈블리 집합과 다른 버전입니다. "
                          + "V19/V20 은 TiaApiServer.V19/V20.exe, V21 은 TiaApiServer.V21.exe 를 쓰세요.",
                    publicApi = PublicApiDir(major)
                });
            }

            SendJson(resp, 200, new {
                active = "V" + selectedMajor,
                activeSource = versionSource,
                loaded = loaded.HasValue ? "V" + loaded.Value : null,
                canSwitch = !loaded.HasValue,
                hint = loaded.HasValue
                    ? "이미 Openness V" + loaded.Value + " 가 로드되어 전환할 수 없습니다. "
                      + "TiaApiServer.exe --tia=19 처럼 인자를 주고 프로세스를 다시 띄우세요."
                    : "아직 Openness 가 로드되지 않았습니다. PUT /api/dll/switch?version=19 로 전환할 수 있습니다.",
                versions = list
            });
        }

        // PUT /api/dll/switch?version=19
        // Openness 가 아직 로드되지 않았을 때만 가능하다. 로드 후에는 .NET 이
        // 어셈블리를 교체할 수 없어 프로세스 재시작 외에는 방법이 없다.
        static void SwitchTiaVersion(HttpListenerContext ctx)
        {
            string raw = ctx.Request.QueryString["version"];
            int major;
            if (string.IsNullOrEmpty(raw)
                || !int.TryParse(raw.Trim().TrimStart('v', 'V').Split('.')[0], out major))
            {
                SendJson(ctx.Response, 400, new { error = "?version=19 처럼 지정하세요" });
                return;
            }

            if (Array.IndexOf(SupportedMajors, major) < 0)
            {
                SendJson(ctx.Response, 400, new {
                    error = "V" + major + " 는 이 바이너리가 지원하지 않습니다",
                    supported = SupportedMajors,
                    detail = "버전마다 Openness 어셈블리 이름이 달라 한 바이너리가 겸할 수 없습니다. "
                           + "V19/V20 은 TiaApiServer.V19/V20.exe, V21 은 TiaApiServer.V21.exe 를 실행하세요."
                });
                return;
            }

            if (!IsVersionInstalled(major))
            {
                SendJson(ctx.Response, 404, new {
                    error = "TIA Portal V" + major + " 의 Openness 어셈블리를 찾을 수 없습니다",
                    expected = PublicApiDir(major)
                });
                return;
            }

            int? loaded = LoadedMajor();
            if (loaded.HasValue)
            {
                // 여기서 거짓말하면 이후 모든 호출이 엉뚱한 버전으로 돌아간다.
                SendJson(ctx.Response, 409, new {
                    error = "이미 Openness V" + loaded.Value + " 가 이 프로세스에 로드되어 있습니다",
                    detail = ".NET 은 로드된 어셈블리를 교체하거나 언로드할 수 없습니다. "
                           + "버전 전환은 프로세스를 다시 띄우는 방법뿐입니다.",
                    restartWith = "TiaApiServer.exe " + port + " --tia=" + major
                });
                return;
            }

            selectedMajor = major;
            versionSource = "api";
            Console.WriteLine("TIA 대상 버전 전환: V" + major);
            SendJson(ctx.Response, 200, new {
                message = "대상 버전을 V" + major + " 로 전환했습니다 (아직 로드 전이라 적용됩니다)",
                active = "V" + major,
                publicApi = PublicApiDir(major)
            });
        }

        public static void Install()
        {
            selectedMajor = ResolveRequestedMajor();

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string assemblyName = new AssemblyName(args.Name).Name + ".dll";

                // 1) 선택된 버전의 PublicAPI
                string tiaFile = Path.Combine(PublicApiDir(selectedMajor), assemblyName);
                if (File.Exists(tiaFile))
                    return Assembly.LoadFrom(tiaFile);

                // 2) 같은 버전의 Bin\PublicAPI (Siemens.Engineering.Contract 등)
                string tiaBinFile = Path.Combine(BinPublicApiDir(selectedMajor), assemblyName);
                if (File.Exists(tiaBinFile))
                    return Assembly.LoadFrom(tiaBinFile);

                return null;
            };
        }
    }
    class Program
    {

        static JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static TiaPortal connectedPortal = null;
        static TiaPortalProcess connectedProcess = null;
        static Dictionary<string, UserGlobalLibrary> openedGlobalLibraries = new Dictionary<string, UserGlobalLibrary>();
        static string tempDir = Path.Combine(Path.GetTempPath(), "TiaApiServer");

        static Dictionary<string, string> langToExt = new Dictionary<string, string>
        {
            { "SCL", ".scl" }, { "DB", ".db" }, { "STL", ".awl" }
        };

        static string MakeValidFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in invalid) name = name.Replace(c, '_');
            return name;
        }

        public static void HandleRequest(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
            string method = ctx.Request.HttpMethod;

            // CORS
            ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, OPTIONS");
            ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            if (method == "OPTIONS") { ctx.Response.StatusCode = 200; ctx.Response.Close(); return; }

            Console.WriteLine(method + " " + path);

            if (path == "" || path == "/") { ServeSwaggerUI(ctx.Response); return; }
            if (path == "/explorer" || path == "/explorer/") { ServeExplorerUI(ctx.Response); return; }
            if (path == "/api/openapi.json") { ServeOpenApiSpec(ctx.Response); return; }
            if (path == "/ui/device-properties") { ServeDevicePropertiesUI(ctx.Response); return; }
            if (path == "/ui/network-properties") { ServeNetworkPropertiesUI(ctx.Response); return; }

            // DLL management
            if (path == "/api/dll/status") { GetDllStatus(ctx.Response); return; }

            // API routes
            if (path == "/api/processes") { GetProcesses(ctx.Response); return; }
            if (path == "/api/connect") { Connect(ctx); return; }
            if (path == "/api/status") { GetStatus(ctx.Response); return; }
            if (path == "/api/debug/portal") { DebugPortal(ctx.Response); return; }

            // ── Project Server / Local Session 전용 API ──
            if (path == "/api/project-server/info") { GetProjectServerInfo(ctx.Response); return; }
            if (path == "/api/project-server/projects") { GetProjectServerProjects(ctx.Response); return; }
            if (path == "/api/project-server/add" && method == "POST") { AddToProjectServer(ctx); return; }
            if (path == "/api/project-server/create-session" && method == "POST") { CreateProjectServerSession(ctx); return; }
            if (path == "/api/project-server/local-sessions") { GetProjectServerLocalSessions(ctx); return; }
            if (path == "/api/project-server/session-modes") { GetSessionCreationModes(ctx.Response); return; }
            if (path == "/api/local-sessions") { ListLocalSessions(ctx.Response); return; }
            if (path == "/api/local-sessions/open" && method == "POST") { OpenLocalSession(ctx); return; }
            if (path == "/api/local-sessions/open-server-project" && method == "POST") { OpenServerProjectAsSession(ctx); return; }
            if (path == "/api/projects") { GetProjects(ctx.Response); return; }
            if (path == "/api/devices") { GetDevices(ctx); return; }
            if (path == "/api/portal/new") { NewPortal(ctx); return; }
            if (path == "/api/project/create") { CreateProject(ctx); return; }
            if (path == "/api/project/add-device") { AddDevice(ctx); return; }
            if (path == "/api/catalog/search") { SearchCatalog(ctx); return; }
            if (path == "/api/catalog/compatible") { SearchCompatible(ctx); return; }
            if (path == "/api/device/items") { GetDeviceItems(ctx); return; }
            if (path == "/api/device/search-attrs") { SearchDeviceAttributes(ctx); return; }
            if (path == "/api/device/mrp") { GetMrpData(ctx); return; }
            if (path == "/api/device/mrp/set-all-roles" && method == "PUT") { SetAllMrpRoles(ctx); return; }
            if (path == "/api/device/_debug") { DumpDevice(ctx); return; }
            if (path == "/api/project/_debug") { DumpProject(ctx); return; }
            if (path == "/api/subnet/_debug") { DumpSubnet(ctx); return; }
            if (path == "/api/device/item-attributes")
            {
                if (method == "PUT") SetDeviceItemAttributes(ctx);
                else GetDeviceItemAttributes(ctx);
                return;
            }
            if (path == "/api/device/set-interface-ip" && method == "PUT") { SetDeviceInterfaceIp(ctx); return; }
            if (path == "/api/network/interfaces") { ListNetworkInterfaces(ctx); return; }
            if (path == "/api/network/subnets" && method == "GET") { ListSubnetsWithNodes(ctx); return; }
            if (path == "/api/network/subnet/rename" && method == "PUT") { RenameSubnet(ctx); return; }
            if (path == "/api/network/subnet/attributes")
            {
                if (method == "PUT") SetSubnetAttributes(ctx);
                else GetSubnetAttributes(ctx);
                return;
            }
            if (path == "/api/network/attach" && method == "POST") { AttachNodeToSubnet(ctx); return; }
            if (path == "/api/network/detach" && method == "POST") { DetachNodeFromSubnet(ctx); return; }
            if (path == "/api/device/subitems") { GetDeviceSubItems(ctx); return; }
            if (path == "/api/device/export") { ExportDeviceConfig(ctx); return; }
            if (path == "/api/project/export-aml") { ExportAml(ctx); return; }
            if (path == "/api/project/import-aml" && method == "POST") { ImportAml(ctx); return; }
            if (path == "/api/device/export-aml") { ExportDeviceAml(ctx); return; }
            if (path == "/api/gsd/list") { ListGsdFiles(ctx); return; }
            if (path == "/api/gsd/read") { ReadGsdFile(ctx); return; }
            if (path == "/api/gsd/parse" && method == "POST") { ParseGsdmlToCatalog(ctx); return; }
            if (path == "/api/drive/objects") { GetDriveObjects(ctx); return; }
            if (path == "/api/drive/parameters") { GetDriveParameters(ctx); return; }
            if (path == "/api/drive/motor-config") { GetMotorConfiguration(ctx); return; }
            if (path == "/api/drive/dump-all") { DumpAllDriveParameters(ctx); return; }
            if (path == "/api/library/list") { GetLibraryList(ctx); return; }
            if (path == "/api/library/export") { ExportLibraryItem(ctx); return; }
            if (path == "/api/library/export/zip") { ExportLibraryZip(ctx); return; }
            if (path == "/api/library/import" && method == "POST") { ImportLibraryItem(ctx); return; }
            if (path == "/api/library/copy-to-plc") { CopyLibraryToPlc(ctx); return; }
            if (path == "/api/library/create-mastercopy") { CreateMasterCopyFromBlock(ctx); return; }
            if (path == "/api/global-library/import" && method == "POST") { ImportGlobalLibraryToProject(ctx); return; }
            if (path == "/api/global-library/to-project-library") { GlobalLibraryToProjectLibrary(ctx); return; }
            if (path == "/api/global-library/update-project-library") { UpdateProjectLibraryFromGlobal(ctx); return; }
            if (path == "/api/global-library/open" && method == "POST") { OpenGlobalLibrary(ctx); return; }
            if (path == "/api/global-library/list") { ListGlobalLibrary(ctx); return; }
            if (path == "/api/global-library/copy-to-project") { CopyGlobalLibraryToProject(ctx); return; }
            if (path == "/api/global-library/close") { CloseGlobalLibrary(ctx); return; }
            if (path.StartsWith("/api/plc/")) { HandlePlcRoute(ctx, path.Substring("/api/plc/".Length)); return; }
            if (path.StartsWith("/api/hmi-unified/")) { HandleHmiUnifiedRoute(ctx, path.Substring("/api/hmi-unified/".Length)); return; }
            if (path.StartsWith("/api/hmi/")) { HandleHmiRoute(ctx, path.Substring("/api/hmi/".Length)); return; }

            SendJson(ctx.Response, 404, new { error = "Not found", path = path });
        }

        // ── DLL Management ──

        static string[] requiredDlls = new[] { "Siemens.Engineering.dll", "Siemens.Engineering.Hmi.dll" };

        static void GetDllStatus(HttpListenerResponse resp)
        {
            var results = new List<object>();
            foreach (string dllName in requiredDlls)
            {
                string tiaPath = Path.Combine(PublicApiDir(selectedMajor), dllName);
                string tiaBinPath = Path.Combine(BinPublicApiDir(selectedMajor), dllName);

                string foundAt = null;
                if (File.Exists(tiaPath)) foundAt = tiaPath;
                else if (File.Exists(tiaBinPath)) foundAt = tiaBinPath;

                results.Add(new { name = dllName, found = foundAt != null, location = foundAt });
            }

            bool allFound = true;
            foreach (var r in results)
            {
                var foundProp = r.GetType().GetProperty("found");
                if (foundProp != null && !(bool)foundProp.GetValue(r)) { allFound = false; break; }
            }
            SendJson(resp, 200, new {
                ready = allFound,
                message = allFound
                    ? "Openness 어셈블리가 모두 준비되었습니다"
                    : "Openness 어셈블리를 찾지 못했습니다. 해당 버전의 TIA Portal 이 설치되어 있는지 확인하세요.",
                tiaVersion = "V" + selectedMajor,
                loadedVersion = LoadedMajor().HasValue ? "V" + LoadedMajor().Value : null,
                dlls = results
            });
        }


        // ── TIA Connection ──

        static void GetProcesses(HttpListenerResponse resp)
        {
            var processes = TiaPortal.GetProcesses().Select(p => new {
                id = p.Id,
                projectPath = p.ProjectPath != null ? p.ProjectPath.ToString() : null
            }).ToList();
            SendJson(resp, 200, new { count = processes.Count, processes });
        }

        static void Connect(HttpListenerContext ctx)
        {
            int processId = -1;
            string qid = ctx.Request.QueryString["processId"];
            if (qid != null) int.TryParse(qid, out processId);

            var processes = TiaPortal.GetProcesses().ToList();
            if (processes.Count == 0)
            {
                SendJson(ctx.Response, 404, new { error = "No TIA Portal process found" });
                return;
            }

            TiaPortalProcess target = processId > 0
                ? processes.FirstOrDefault(p => p.Id == processId) ?? processes[0]
                : processes[0];

            connectedProcess = target;
            connectedPortal = target.Attach();
            SendJson(ctx.Response, 200, new {
                message = "Connected",
                processId = target.Id,
                projectPath = target.ProjectPath != null ? target.ProjectPath.ToString() : null
            });
        }

        static void GetStatus(HttpListenerResponse resp)
        {
            SendJson(resp, 200, new {
                connected = connectedPortal != null,
                projects = connectedPortal != null ? connectedPortal.Projects.Count : 0
            });
        }

        // 연결된 TiaPortal 과 그 하위 컬렉션의 public 속성을 dump 한다.
        // Multiuser/ProjectServer 경유로 열린 프로젝트가 어느 컬렉션에 있는지 확인 용도.
        // depth 제한을 두고 순환 방지용 visited set 사용.
        static void DebugPortal(HttpListenerResponse resp)
        {
            EnsureConnected();
            var portal = connectedPortal;
            var visited = new HashSet<object>(new ReferenceEqualityComparer());

            // 1) 모든 TIA Portal 프로세스 목록
            List<object> allProcesses = new List<object>();
            try
            {
                foreach (var p in TiaPortal.GetProcesses())
                {
                    allProcesses.Add(new
                    {
                        processId = p.Id,
                        projectPath = p.ProjectPath != null ? p.ProjectPath.ToString() : null,
                        mode = p.Mode.ToString()
                    });
                }
            }
            catch (Exception ex) { allProcesses.Add(new { error = ex.Message }); }

            // 2) 현재 attach 된 프로세스
            object attachedProcessInfo = null;
            if (connectedProcess != null)
            {
                try
                {
                    attachedProcessInfo = new
                    {
                        processId = connectedProcess.Id,
                        projectPath = connectedProcess.ProjectPath != null ? connectedProcess.ProjectPath.ToString() : null,
                        mode = connectedProcess.Mode.ToString()
                    };
                }
                catch (Exception ex) { attachedProcessInfo = new { error = ex.Message }; }
            }

            // 3) Portal property dump (기존)
            var portalDump = DumpObject(portal, 0, 3, visited);

            // 4) ProjectServer 상세 — 메서드 포함 + 매개변수 없는 메서드 호출 시도
            object projectServerDetail = null;
            try
            {
                var psColl = portal.GetType().GetProperty("ProjectServers").GetValue(portal) as System.Collections.IEnumerable;
                object firstPs = null;
                foreach (var it in psColl) { firstPs = it; break; }
                if (firstPs != null)
                    projectServerDetail = DumpTypeMembers(firstPs);
            }
            catch (Exception ex) { projectServerDetail = new { error = ex.Message }; }

            // 5) LocalSessions 상세도 함께
            object localSessionsDetail = null;
            try
            {
                var ls = portal.GetType().GetProperty("LocalSessions").GetValue(portal);
                if (ls != null) localSessionsDetail = DumpTypeMembers(ls);
            }
            catch (Exception ex) { localSessionsDetail = new { error = ex.Message }; }

            SendJson(resp, 200, new
            {
                attachedProcess = attachedProcessInfo,
                allProcesses = allProcesses,
                portal = portalDump,
                projectServerMembers = projectServerDetail,
                localSessionsMembers = localSessionsDetail
            });
        }

        // 객체의 GetType() 기준 메서드/프로퍼티 리스트와, 매개변수 없는 메서드 호출 결과를 dump.
        // Openness 에서 reflection 으로만 보이는 숨은 API (ProjectServer.GetServerProjects 등) 탐색 용도.
        static object DumpTypeMembers(object obj)
        {
            var t = obj.GetType();
            var flags = BindingFlags.Public | BindingFlags.Instance;

            var methods = t.GetMethods(flags)
                .Where(m => !m.IsSpecialName)
                .Select(m => (object)new
                {
                    name = m.Name,
                    paramCount = m.GetParameters().Length,
                    parameters = m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name).ToArray(),
                    returnType = m.ReturnType.FullName,
                    declaringType = m.DeclaringType.FullName
                })
                .ToList();

            var properties = t.GetProperties(flags)
                .Select(p => (object)new
                {
                    name = p.Name,
                    type = p.PropertyType.FullName,
                    canRead = p.CanRead,
                    canWrite = p.CanWrite
                })
                .ToList();

            // 매개변수 없는 메서드들을 호출해서 결과 확인
            var zeroArgResults = new Dictionary<string, object>();
            foreach (var m in t.GetMethods(flags))
            {
                if (m.IsSpecialName) continue;
                if (m.GetParameters().Length != 0) continue;
                if (m.Name == "GetType" || m.Name == "ToString" || m.Name == "GetHashCode" || m.Name == "Dispose") continue;
                try
                {
                    var res = m.Invoke(obj, null);
                    if (res == null) { zeroArgResults[m.Name] = null; continue; }
                    if (res is string) { zeroArgResults[m.Name] = res; continue; }
                    if (res is System.Collections.IEnumerable en)
                    {
                        int c = 0;
                        var firstFew = new List<string>();
                        foreach (var it in en)
                        {
                            c++;
                            if (firstFew.Count < 5)
                                firstFew.Add(it == null ? null : it.GetType().FullName);
                        }
                        zeroArgResults[m.Name] = new { __type = res.GetType().FullName, count = c, firstItemTypes = firstFew };
                    }
                    else
                    {
                        zeroArgResults[m.Name] = new { __type = res.GetType().FullName, value = res.ToString() };
                    }
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException ?? ex;
                    zeroArgResults[m.Name] = new { error = inner.GetType().FullName + ": " + inner.Message };
                }
            }

            return new
            {
                typeName = t.FullName,
                methodCount = methods.Count,
                methods = methods,
                propertyCount = properties.Count,
                properties = properties,
                zeroArgMethodResults = zeroArgResults
            };
        }

        class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object obj) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj); }
        }

        // ─────────────────────────────────────────────────────────────────
        //   Project Server / Local Session 전용 API
        //   AutomationFramework 같은 서버 프로젝트는 TiaPortal.Projects 에도
        //   TiaPortal.LocalSessions 에도 기본으로 잡히지 않는다. 그런 프로젝트를
        //   ProjectServer.GetServerProjects → CreateLocalSession 또는
        //   LocalSessions.OpenServerProject 경로로 열기 위한 전용 엔드포인트.
        // ─────────────────────────────────────────────────────────────────

        // 문자열/프리미티브/Enum 타입 public property만 얕게 추출
        static Dictionary<string, object> ExtractSimpleProperties(object obj)
        {
            var d = new Dictionary<string, object>();
            if (obj == null) return d;
            d["__type"] = obj.GetType().FullName;
            foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                var pt = p.PropertyType;
                bool simple = pt == typeof(string) || pt.IsPrimitive || pt.IsEnum || pt == typeof(DateTime) || pt == typeof(Guid);
                if (!simple) continue;
                try
                {
                    var v = p.GetValue(obj);
                    d[p.Name] = v == null ? null : (pt.IsEnum ? v.ToString() : v);
                }
                catch { /* ignore */ }
            }
            return d;
        }

        static object GetPropValue(object obj, string name)
        {
            if (obj == null) return null;
            var p = obj.GetType().GetProperty(name);
            if (p == null) return null;
            try { return p.GetValue(obj); } catch { return null; }
        }

        static ProjectServer GetDefaultProjectServer()
        {
            EnsureConnected();
            foreach (ProjectServer s in connectedPortal.ProjectServers) return s;
            throw new Exception("No ProjectServer configured on this TiaPortal instance.");
        }

        // GET /api/project-server/info — 기본 서버 연결 가능 여부 확인
        static void GetProjectServerInfo(HttpListenerResponse resp)
        {
            EnsureConnected();
            var servers = new List<object>();
            foreach (ProjectServer s in connectedPortal.ProjectServers)
            {
                object reachable;
                string error = null;
                try { s.GetServerProjects(); reachable = true; }
                catch (Exception ex) { reachable = false; error = ex.Message; }
                servers.Add(new
                {
                    host = s.Host,
                    port = s.Port,
                    serverName = s.ServerName,
                    reachable = reachable,
                    error = error
                });
            }
            SendJson(resp, 200, new { count = servers.Count, servers = servers });
        }

        // GET /api/project-server/projects — 서버에 등록된 프로젝트 목록
        static void GetProjectServerProjects(HttpListenerResponse resp)
        {
            ProjectServer server;
            try { server = GetDefaultProjectServer(); }
            catch (Exception ex) { SendJson(resp, 404, new { error = ex.Message }); return; }
            try
            {
                var infos = server.GetServerProjects();
                var list = new List<object>();
                foreach (var info in infos) list.Add(ExtractSimpleProperties(info));
                SendJson(resp, 200, new { count = list.Count, projects = list });
            }
            catch (Exception ex)
            {
                SendJson(resp, 500, new { error = ex.Message, type = ex.GetType().FullName });
            }
        }

        // POST /api/project-server/add?path=...  — .ap20 파일을 서버에 등록
        static void AddToProjectServer(HttpListenerContext ctx)
        {
            var path = ctx.Request.QueryString["path"];
            if (string.IsNullOrEmpty(path)) { SendJson(ctx.Response, 400, new { error = "path query parameter required" }); return; }
            if (!File.Exists(path)) { SendJson(ctx.Response, 404, new { error = "File not found", path }); return; }
            ProjectServer server;
            try { server = GetDefaultProjectServer(); }
            catch (Exception ex) { SendJson(ctx.Response, 404, new { error = ex.Message }); return; }
            try
            {
                server.AddProjectToServer(new FileInfo(path));
                SendJson(ctx.Response, 200, new { message = "Added to server", path });
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = ex.Message, type = ex.GetType().FullName });
            }
        }

        // POST /api/project-server/create-session
        //   ?projectName=...&sessionName=...&path=...&mode=...
        //   mode 값은 /api/project-server/session-modes 로 조회 가능
        static void CreateProjectServerSession(HttpListenerContext ctx)
        {
            var q = ctx.Request.QueryString;
            string projectName = q["projectName"];
            string sessionName = q["sessionName"];
            string sessionPath = q["path"] ?? q["localSessionPath"];
            string modeStr = q["mode"];
            if (string.IsNullOrEmpty(projectName) || string.IsNullOrEmpty(sessionName) ||
                string.IsNullOrEmpty(sessionPath) || string.IsNullOrEmpty(modeStr))
            {
                SendJson(ctx.Response, 400, new
                {
                    error = "Required query params: projectName, sessionName, path, mode"
                });
                return;
            }
            SessionCreationMode mode;
            if (!Enum.TryParse<SessionCreationMode>(modeStr, out mode))
            {
                SendJson(ctx.Response, 400, new
                {
                    error = "Invalid mode",
                    allowed = Enum.GetNames(typeof(SessionCreationMode))
                });
                return;
            }
            ProjectServer server;
            try { server = GetDefaultProjectServer(); }
            catch (Exception ex) { SendJson(ctx.Response, 404, new { error = ex.Message }); return; }

            try
            {
                var infos = server.GetServerProjects();
                object match = null;
                var available = new List<string>();
                foreach (var info in infos)
                {
                    var n = GetPropValue(info, "Name") as string;
                    available.Add(n);
                    if (n == projectName) { match = info; break; }
                }
                if (match == null)
                {
                    SendJson(ctx.Response, 404, new { error = "Server project not found", projectName, available });
                    return;
                }
                var sessionInfo = server.CreateLocalSession(
                    (ServerProjectInfo)match,
                    sessionName,
                    new DirectoryInfo(sessionPath),
                    mode);
                SendJson(ctx.Response, 200, new
                {
                    message = "Session created",
                    sessionInfo = ExtractSimpleProperties(sessionInfo)
                });
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = ex.Message, type = ex.GetType().FullName });
            }
        }

        // GET /api/project-server/local-sessions?projectName=...
        // 특정 서버 프로젝트의 기존 로컬 세션 목록 (체크아웃 이력)
        static void GetProjectServerLocalSessions(HttpListenerContext ctx)
        {
            var projectName = ctx.Request.QueryString["projectName"];
            if (string.IsNullOrEmpty(projectName)) { SendJson(ctx.Response, 400, new { error = "projectName query parameter required" }); return; }
            ProjectServer server;
            try { server = GetDefaultProjectServer(); }
            catch (Exception ex) { SendJson(ctx.Response, 404, new { error = ex.Message }); return; }
            try
            {
                var infos = server.GetServerProjects();
                object match = null;
                foreach (var info in infos)
                {
                    var n = GetPropValue(info, "Name") as string;
                    if (n == projectName) { match = info; break; }
                }
                if (match == null) { SendJson(ctx.Response, 404, new { error = "Server project not found", projectName }); return; }
                var sessions = server.GetLocalSessions((ServerProjectInfo)match);
                var list = new List<object>();
                foreach (var s in sessions) list.Add(ExtractSimpleProperties(s));
                SendJson(ctx.Response, 200, new { count = list.Count, sessions = list });
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = ex.Message, type = ex.GetType().FullName });
            }
        }

        // GET /api/project-server/session-modes  —  SessionCreationMode enum 값 목록
        static void GetSessionCreationModes(HttpListenerResponse resp)
        {
            SendJson(resp, 200, new { modes = Enum.GetNames(typeof(SessionCreationMode)) });
        }

        // GET /api/local-sessions  —  portal.LocalSessions 순회
        static void ListLocalSessions(HttpListenerResponse resp)
        {
            EnsureConnected();
            var list = new List<object>();
            foreach (LocalSession s in connectedPortal.LocalSessions)
            {
                object projInfo = null;
                try { projInfo = s.Project == null ? null : (object)ExtractSimpleProperties(s.Project); }
                catch (Exception ex) { projInfo = new { error = ex.Message }; }
                list.Add(new
                {
                    session = ExtractSimpleProperties(s),
                    project = projInfo
                });
            }
            SendJson(resp, 200, new { count = list.Count, sessions = list });
        }

        // POST /api/local-sessions/open?path=...  —  .als20 세션 파일 열기
        static void OpenLocalSession(HttpListenerContext ctx)
        {
            var path = ctx.Request.QueryString["path"];
            if (string.IsNullOrEmpty(path)) { SendJson(ctx.Response, 400, new { error = "path query parameter required" }); return; }
            if (!File.Exists(path)) { SendJson(ctx.Response, 404, new { error = "File not found", path }); return; }
            EnsureConnected();
            try
            {
                var session = connectedPortal.LocalSessions.Open(new FileInfo(path));
                SendJson(ctx.Response, 200, new
                {
                    message = "Opened local session",
                    session = ExtractSimpleProperties(session),
                    project = session.Project == null ? null : (object)ExtractSimpleProperties(session.Project)
                });
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = ex.Message, type = ex.GetType().FullName });
            }
        }

        // POST /api/local-sessions/open-server-project?path=...
        static void OpenServerProjectAsSession(HttpListenerContext ctx)
        {
            var path = ctx.Request.QueryString["path"];
            if (string.IsNullOrEmpty(path)) { SendJson(ctx.Response, 400, new { error = "path query parameter required" }); return; }
            if (!File.Exists(path)) { SendJson(ctx.Response, 404, new { error = "File not found", path }); return; }
            EnsureConnected();
            try
            {
                var session = connectedPortal.LocalSessions.OpenServerProject(new FileInfo(path));
                SendJson(ctx.Response, 200, new
                {
                    message = "Opened server project as local session",
                    session = ExtractSimpleProperties(session),
                    project = session.Project == null ? null : (object)ExtractSimpleProperties(session.Project)
                });
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = ex.Message, type = ex.GetType().FullName });
            }
        }

        static object DumpObject(object obj, int depth, int maxDepth, HashSet<object> visited)
        {
            if (obj == null) return null;
            if (obj is string) return obj;
            if (obj.GetType().IsPrimitive || obj.GetType().IsEnum) return obj;

            if (!visited.Add(obj)) return new { __type = obj.GetType().FullName, __cycle = true };

            // IEnumerable 먼저 처리
            if (obj is System.Collections.IEnumerable enumerable)
            {
                var items = new List<object>();
                int count = 0;
                foreach (var item in enumerable)
                {
                    if (count >= 50) break;
                    if (depth + 1 > maxDepth)
                    {
                        items.Add(item == null ? null : (object)(item.GetType().FullName));
                    }
                    else
                    {
                        items.Add(DumpObject(item, depth + 1, maxDepth, visited));
                    }
                    count++;
                }
                return new { __type = obj.GetType().FullName, count = count, items = items };
            }

            // 일반 객체 — public 인스턴스 프로퍼티 덤프
            var result = new Dictionary<string, object>();
            result["__type"] = obj.GetType().FullName;

            foreach (var prop in obj.GetType().GetProperties())
            {
                if (prop.Name == "Item") continue; // indexer
                if (prop.GetIndexParameters().Length > 0) continue;
                try
                {
                    var val = prop.GetValue(obj);
                    var pt = prop.PropertyType;

                    if (val == null) { result[prop.Name] = null; continue; }
                    if (pt == typeof(string) || pt.IsPrimitive || pt.IsEnum)
                    {
                        result[prop.Name] = val;
                        continue;
                    }

                    if (depth + 1 > maxDepth)
                    {
                        result[prop.Name] = new { __type = val.GetType().FullName, __truncated = true };
                    }
                    else
                    {
                        result[prop.Name] = DumpObject(val, depth + 1, maxDepth, visited);
                    }
                }
                catch (Exception ex)
                {
                    result[prop.Name] = new { error = ex.Message };
                }
            }

            return result;
        }

        static void EnsureConnected()
        {
            if (connectedPortal != null) return;
            var processes = TiaPortal.GetProcesses().ToList();
            if (processes.Count > 0)
            {
                connectedProcess = processes[0];
                connectedPortal = connectedProcess.Attach();
            }
            if (connectedPortal == null)
                throw new Exception("Not connected to TIA Portal. Call /api/connect first.");
        }

        // ── Portal / Project / Device Creation ──

        static void NewPortal(HttpListenerContext ctx)
        {
            string modeStr = ctx.Request.QueryString["mode"] ?? "WithUserInterface";
            TiaPortalMode mode = modeStr == "WithoutUserInterface"
                ? TiaPortalMode.WithoutUserInterface
                : TiaPortalMode.WithUserInterface;

            Console.WriteLine("Starting new TIA Portal (" + mode + ")...");
            var newPortal = new TiaPortal(mode);
            connectedPortal = newPortal;
            SendJson(ctx.Response, 200, new {
                message = "New TIA Portal started and connected",
                mode = mode.ToString()
            });
        }

        static void CreateProject(HttpListenerContext ctx)
        {
            string projectDir = ctx.Request.QueryString["path"];
            string projectName = ctx.Request.QueryString["name"];
            if (string.IsNullOrEmpty(projectDir) || string.IsNullOrEmpty(projectName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?path= and ?name= parameters" });
                return;
            }
            EnsureConnected();

            var dirInfo = new DirectoryInfo(projectDir);
            if (!dirInfo.Exists) dirInfo.Create();

            Console.WriteLine("Creating project: " + projectName + " at " + projectDir);
            var project = connectedPortal.Projects.Create(dirInfo, projectName);
            SendJson(ctx.Response, 200, new {
                message = "Project created",
                name = project.Name,
                path = project.Path != null ? project.Path.ToString() : null
            });
        }

        static void AddDevice(HttpListenerContext ctx)
        {
            string typeId = ctx.Request.QueryString["type"] ?? "System:Device.S71500";
            string deviceName = ctx.Request.QueryString["name"] ?? "NewDevice";
            string orderNumber = ctx.Request.QueryString["order"] ?? "OrderNumber:6ES7 517-3AP00-0AB0/V3.1";
            string projectName = ctx.Request.QueryString["project"];

            EnsureConnected();
            Project project = null;
            foreach (Project p in connectedPortal.Projects)
            {
                if (projectName == null || p.Name == projectName) { project = p; break; }
            }
            if (project == null) { SendJson(ctx.Response, 404, new { error = "No project found" }); return; }

            Console.WriteLine("Adding device: " + deviceName + " type=" + typeId + " order=" + orderNumber);
            var device = project.Devices.CreateWithItem(typeId, deviceName, orderNumber);

            // Find PLC software in the new device
            string plcItemName = null;
            foreach (DeviceItem di in device.DeviceItems)
            {
                var sc = ((IEngineeringServiceProvider)di).GetService<SoftwareContainer>();
                if (sc != null && sc.Software is PlcSoftware)
                {
                    plcItemName = di.Name;
                    break;
                }
            }

            SendJson(ctx.Response, 200, new {
                message = "Device added",
                deviceName = device.Name,
                typeIdentifier = device.TypeIdentifier,
                plcItemName = plcItemName
            });
        }

        // ── Projects & Devices ──

        static void GetProjects(HttpListenerResponse resp)
        {
            EnsureConnected();
            var projects = connectedPortal.Projects.Select(p => new {
                name = p.Name,
                path = p.Path != null ? p.Path.ToString() : null
            }).ToList();
            SendJson(resp, 200, new { count = projects.Count, projects });
        }

        static void GetDevices(HttpListenerContext ctx)
        {
            EnsureConnected();
            string projectName = ctx.Request.QueryString["project"];
            var devices = new List<object>();
            foreach (Project proj in connectedPortal.Projects)
            {
                if (projectName != null && proj.Name != projectName) continue;
                foreach (Device device in EnumerateAllDevices(proj))
                {
                    var deviceItems = new List<object>();
                    // 재귀로 모든 DeviceItem 트리를 보면서 software 가진 것을 찾음
                    // (Unified HMI 의 HmiSoftware 는 가끔 더 깊은 child 에 있음)
                    CollectDeviceItemsWithSoftware(device.DeviceItems, deviceItems);
                    devices.Add(new {
                        name = device.Name,
                        type = device.TypeIdentifier,
                        project = proj.Name,
                        items = deviceItems
                    });
                }
            }
            SendJson(ctx.Response, 200, new { count = devices.Count, devices });
        }

        // ── PLC Routes ──

        static PlcSoftware FindPlc(string deviceItemName)
        {
            EnsureConnected();
            foreach (Project proj in connectedPortal.Projects)
            {
                foreach (Device device in proj.Devices)
                {
                    foreach (DeviceItem di in device.DeviceItems)
                    {
                        if (di.Name == deviceItemName)
                        {
                            var sc = ((IEngineeringServiceProvider)di).GetService<SoftwareContainer>();
                            if (sc != null && sc.Software is PlcSoftware plc) return plc;
                        }
                    }
                }
            }
            return null;
        }

        static void HandlePlcRoute(HttpListenerContext ctx, string route)
        {
            string plcName = ctx.Request.QueryString["plc"];
            if (string.IsNullOrEmpty(plcName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?plc= parameter" });
                return;
            }

            // 통합 compile — PLC / Comfort HMI / Unified HMI 모두 자동 분기
            // (PLC 라우트지만 의도적으로 device 종류를 자동 감지하여 통합 처리)
            if (route == "compile")
            {
                var plcSw = FindPlc(plcName);
                if (plcSw != null) { CompileResource(ctx, plcName, plcSw, "PlcSoftware"); return; }
                var hmi = FindHmi(plcName);
                if (hmi != null) { CompileResource(ctx, plcName, hmi, "HmiTarget"); return; }
                var unified = FindHmiSoftware(plcName);
                if (unified != null) { CompileResource(ctx, plcName, unified, "HmiSoftware"); return; }
                SendJson(ctx.Response, 404, new { error = "디바이스를 찾을 수 없음 (PLC / Comfort HMI / Unified HMI 모두 시도): " + plcName });
                return;
            }

            var plc = FindPlc(plcName);
            if (plc == null)
            {
                SendJson(ctx.Response, 404, new { error = "PLC not found: " + plcName });
                return;
            }

            if (route == "blocks") { GetBlocks(ctx.Response, plc.BlockGroup, ""); return; }
            if (route == "types") { GetTypes(ctx.Response, plc); return; }
            if (route == "tags") { GetTagTables(ctx.Response, plc); return; }
            if (route == "units") { GetSoftwareUnits(ctx.Response, plc); return; }
            if (route.StartsWith("units/"))
            {
                string unitPath = route.Substring("units/".Length);
                string unitName = Uri.UnescapeDataString(unitPath.Split('/')[0]);
                string subRoute = unitPath.Contains("/") ? unitPath.Substring(unitPath.IndexOf('/') + 1) : "blocks";

                if (subRoute == "export")
                {
                    ExportUnitZip(ctx.Response, plc, unitName);
                    return;
                }
                GetUnitDetail(ctx.Response, plc, unitName, subRoute);
                return;
            }
            if (route == "block-detail")
            {
                string blockName = ctx.Request.QueryString["name"];
                GetBlockDetail(ctx.Response, plc, blockName);
                return;
            }
            if (route == "export/block")
            {
                string blockName = ctx.Request.QueryString["name"];
                string unitName = ctx.Request.QueryString["unit"];
                string format = ctx.Request.QueryString["format"]; // "source" | "xml" | (auto)
                ExportSingleBlock(ctx.Response, plc, blockName, unitName, format);
                return;
            }
            if (route == "tree") { GetPlcTree(ctx.Response, plc); return; }
            if (route == "export/selected")
            {
                bool selCompile = (ctx.Request.QueryString["compile"] ?? "false").ToLower() == "true";
                ExportSelectedXmlZip(ctx, plc, selCompile);
                return;
            }
            if (route == "export/xml")
            {
                string root = (ctx.Request.QueryString["root"] ?? "all").ToLower();
                // ?compile=true 면 export 전에 software compile 한 번 실행 (inconsistent block/UDT 자동 해결 시도)
                bool compileFirst = (ctx.Request.QueryString["compile"] ?? "false").ToLower() == "true";
                ExportAllXmlZip(ctx.Response, plc, root, compileFirst);
                return;
            }
            if (route.StartsWith("units/") && route.EndsWith("/export-xml"))
            {
                string unitPath = route.Substring("units/".Length);
                string uName = Uri.UnescapeDataString(unitPath.Substring(0, unitPath.Length - "/export-xml".Length));
                ExportUnitXmlZip(ctx.Response, plc, uName);
                return;
            }
            if (route == "create-unit")
            {
                string unitName = ctx.Request.QueryString["name"];
                string unitNamespace = ctx.Request.QueryString["namespace"];
                CreateUnit(ctx.Response, plc, unitName, unitNamespace);
                return;
            }
            if (route == "prodiag/create-test")
            {
                string blockName = ctx.Request.QueryString["blockName"];
                string numberStr = ctx.Request.QueryString["number"];
                string langStr = ctx.Request.QueryString["lang"];
                CreateProDiagFbTest(ctx.Response, plc, blockName, numberStr, langStr);
                return;
            }
            if (route == "prodiag/add-supervision-test")
            {
                string blockName = ctx.Request.QueryString["block"];
                string supervisionName = ctx.Request.QueryString["name"];
                AddSupervisionTest(ctx.Response, plc, blockName, supervisionName);
                return;
            }
            // ── Technology Objects ──
            if (route == "techno-objects") { GetTechnoObjects(ctx.Response, plc); return; }
            if (route == "techno-object/export")
            {
                string toName = ctx.Request.QueryString["name"];
                bool toCompile = (ctx.Request.QueryString["compile"] ?? "false").ToLower() == "true";
                ExportTechnoObject(ctx.Response, plc, toName, toCompile);
                return;
            }
            if (route == "techno-objects/export/zip")
            {
                bool toCompileAll = (ctx.Request.QueryString["compile"] ?? "false").ToLower() == "true";
                ExportTechnoObjectsZip(ctx.Response, plc, toCompileAll);
                return;
            }
            if (route == "techno-object/import")
            {
                string groupPath = ctx.Request.QueryString["group"];
                string modeStr = ctx.Request.QueryString["mode"];
                ImportTechnoObjectUpload(ctx, plc, groupPath, modeStr);
                return;
            }
            if (route == "techno-object/create-from-mastercopy")
            {
                string mcPath = ctx.Request.QueryString["mc"];
                string groupPath = ctx.Request.QueryString["group"];
                CreateTechnoObjectFromMasterCopy(ctx.Response, plc, mcPath, groupPath);
                return;
            }
            if (route == "import/xml")
            {
                string filePath = ctx.Request.QueryString["file"];
                ImportXml(ctx.Response, plc, filePath);
                return;
            }
            if (route == "import/upload")
            {
                ImportUpload(ctx, plc);
                return;
            }

            SendJson(ctx.Response, 404, new { error = "Unknown PLC route: " + route });
        }

        static void GetBlocks(HttpListenerResponse resp, PlcBlockGroup group, string path)
        {
            var result = CollectBlocks(group, path);
            SendJson(resp, 200, new { count = result.Count, blocks = result });
        }

        static List<object> CollectBlocks(PlcBlockGroup group, string path)
        {
            var result = new List<object>();
            string currentPath = string.IsNullOrEmpty(path) ? group.Name : path + "/" + group.Name;

            foreach (PlcBlock block in group.Blocks)
            {
                result.Add(new {
                    name = block.Name,
                    number = block.Number,
                    type = block.GetType().Name,
                    language = block.ProgrammingLanguage.ToString(),
                    path = currentPath
                });
            }
            foreach (PlcBlockGroup sub in group.Groups)
            {
                result.AddRange(CollectBlocks(sub, currentPath));
            }
            return result;
        }

        static void GetBlockDetail(HttpListenerResponse resp, PlcSoftware plc, string blockName)
        {
            if (string.IsNullOrEmpty(blockName))
            {
                SendJson(resp, 400, new { error = "Missing ?name= parameter" });
                return;
            }
            var block = FindBlock(plc.BlockGroup, blockName);
            if (block == null)
            {
                SendJson(resp, 404, new { error = "Block not found: " + blockName });
                return;
            }
            SendJson(resp, 200, new {
                name = block.Name,
                number = block.Number,
                type = block.GetType().Name,
                language = block.ProgrammingLanguage.ToString(),
                memoryLayout = block.MemoryLayout.ToString(),
                autoNumber = block.AutoNumber
            });
        }

        static PlcBlock FindBlock(PlcBlockGroup group, string name)
        {
            foreach (PlcBlock b in group.Blocks)
                if (b.Name == name) return b;
            foreach (PlcBlockGroup sub in group.Groups)
            {
                var found = FindBlock(sub, name);
                if (found != null) return found;
            }
            return null;
        }

        static void GetTypes(HttpListenerResponse resp, PlcSoftware plc)
        {
            var types = new List<object>();
            foreach (PlcType t in plc.TypeGroup.Types)
            {
                types.Add(new { name = t.Name });
            }
            SendJson(resp, 200, new { count = types.Count, types });
        }

        static void GetTagTables(HttpListenerResponse resp, PlcSoftware plc)
        {
            var tables = new List<object>();
            CollectTagTables(plc.TagTableGroup.TagTables, tables);
            foreach (PlcTagTableUserGroup ug in plc.TagTableGroup.Groups)
                CollectTagTablesFromGroup(ug, tables);
            SendJson(resp, 200, new { count = tables.Count, tagTables = tables });
        }

        static void CollectTagTables(PlcTagTableComposition comp, List<object> result)
        {
            foreach (PlcTagTable table in comp)
            {
                var tags = new List<object>();
                foreach (PlcTag tag in table.Tags)
                {
                    tags.Add(new {
                        name = tag.Name,
                        dataType = tag.DataTypeName,
                        address = tag.LogicalAddress
                    });
                }
                result.Add(new { name = table.Name, tagCount = tags.Count, tags });
            }
        }

        static void CollectTagTablesFromGroup(PlcTagTableUserGroup group, List<object> result)
        {
            CollectTagTables(group.TagTables, result);
            foreach (PlcTagTableUserGroup sub in group.Groups)
                CollectTagTablesFromGroup(sub, result);
        }

        // ── Software Units ──

        static void GetSoftwareUnits(HttpListenerResponse resp, PlcSoftware plc)
        {
            var unitProvider = plc.GetService<PlcUnitProvider>();
            if (unitProvider == null)
            {
                SendJson(resp, 200, new { count = 0, units = new List<object>(), message = "No software units support" });
                return;
            }
            var units = new List<object>();
            foreach (PlcUnit unit in unitProvider.UnitGroup.Units)
            {
                units.Add(new { name = unit.Name, kind = "unit" });
            }
            foreach (PlcSafetyUnit su in unitProvider.UnitGroup.SafetyUnits)
            {
                units.Add(new { name = su.Name, kind = "safety" });
            }
            SendJson(resp, 200, new { count = units.Count, units });
        }

        static void GetUnitDetail(HttpListenerResponse resp, PlcSoftware plc, string unitName, string subRoute)
        {
            var unitProvider = plc.GetService<PlcUnitProvider>();
            if (unitProvider == null) { SendJson(resp, 404, new { error = "No software units" }); return; }

            PlcBlockGroup blockGroup = null;
            PlcTypeSystemGroup typeGroup = null;
            PlcTagTableSystemGroup tagGroup = null;

            foreach (PlcUnit unit in unitProvider.UnitGroup.Units)
            {
                if (unit.Name == unitName)
                {
                    blockGroup = unit.BlockGroup;
                    typeGroup = unit.TypeGroup;
                    tagGroup = unit.TagTableGroup;
                    break;
                }
            }
            if (blockGroup == null)
            {
                foreach (PlcSafetyUnit su in unitProvider.UnitGroup.SafetyUnits)
                {
                    if (su.Name == unitName)
                    {
                        blockGroup = su.BlockGroup;
                        typeGroup = su.TypeGroup;
                        tagGroup = su.TagTableGroup;
                        break;
                    }
                }
            }
            if (blockGroup == null) { SendJson(resp, 404, new { error = "Unit not found: " + unitName }); return; }

            if (subRoute == "blocks")
            {
                var blocks = CollectBlocks(blockGroup, "");
                SendJson(resp, 200, new { unit = unitName, count = blocks.Count, blocks });
            }
            else if (subRoute == "types")
            {
                var types = new List<object>();
                foreach (PlcType t in typeGroup.Types) types.Add(new { name = t.Name });
                SendJson(resp, 200, new { unit = unitName, count = types.Count, types });
            }
            else if (subRoute == "tags")
            {
                var tables = new List<object>();
                CollectTagTables(tagGroup.TagTables, tables);
                SendJson(resp, 200, new { unit = unitName, count = tables.Count, tagTables = tables });
            }
            else
            {
                SendJson(resp, 404, new { error = "Unknown sub-route: " + subRoute });
            }
        }

        // ── Import ──

        // ProDiag FB 생성 시도 (V20 에서 실제 동작 여부 미확인 — 검증용)
        // POST /api/plc/prodiag/create-test?plc=&blockName=&number=&lang=ProDiag|ProDiag_OB
        static void CreateProDiagFbTest(HttpListenerResponse resp, PlcSoftware plc, string blockName, string numberStr, string langStr)
        {
            if (string.IsNullOrEmpty(blockName))
            {
                SendJson(resp, 400, new { error = "Missing ?blockName= parameter" });
                return;
            }
            int number = 0;
            if (!string.IsNullOrEmpty(numberStr)) int.TryParse(numberStr, out number);
            bool autoNumber = number == 0;

            ProgrammingLanguage lang = ProgrammingLanguage.ProDiag;
            if (!string.IsNullOrEmpty(langStr))
            {
                try { lang = (ProgrammingLanguage)Enum.Parse(typeof(ProgrammingLanguage), langStr, true); }
                catch (Exception ex) { SendJson(resp, 400, new { error = "Invalid lang: " + langStr + " (" + ex.Message + ")", validValues = Enum.GetNames(typeof(ProgrammingLanguage)) }); return; }
            }

            object createdFb = null;
            string createError = null;
            string innerError = null;
            try
            {
                createdFb = plc.BlockGroup.Blocks.CreateFB(blockName, autoNumber, number, lang);
            }
            catch (Exception ex)
            {
                createError = ex.Message;
                innerError = ex.InnerException != null ? ex.InnerException.Message : null;
            }

            if (createdFb == null)
            {
                SendJson(resp, 500, new
                {
                    blockName,
                    requestedLanguage = lang.ToString(),
                    status = "create_failed",
                    error = createError ?? "CreateFB returned null",
                    innerError,
                    hint = "ProDiag/ProDiag_OB 가 CreateFB 에 대해 거부된다면 Block.Export() 와 같이 Openness 의 명시적 제약. supervision XLSX import 경로를 통해 ProDiag 자동 생성을 시도해야 함."
                });
                return;
            }

            // 생성된 FB 의 attribute / composition / service 모두 dump (AssignedProDiagFB 등 발견용)
            var ieo = (IEngineeringObject)createdFb;
            var attrs = ReadAllAttributesOf(ieo);
            var compositions = new List<object>();
            try
            {
                foreach (var ci in ieo.GetCompositionInfos())
                {
                    string compName = null;
                    try { compName = ci.GetType().GetProperty("Name")?.GetValue(ci, null) as string; } catch { }
                    if (string.IsNullOrEmpty(compName)) continue;
                    int count = -1;
                    try
                    {
                        var comp = ieo.GetComposition(compName);
                        var cp = comp?.GetType().GetProperty("Count");
                        if (cp != null) count = Convert.ToInt32(cp.GetValue(comp, null));
                    }
                    catch { }
                    compositions.Add(new { name = compName, count });
                }
            }
            catch { }

            var serviceInfos = new List<string>();
            try
            {
                var sip = createdFb as IEngineeringServiceProvider;
                if (sip != null)
                {
                    var sInfos = sip.GetServiceInfos() as System.Collections.IEnumerable;
                    if (sInfos != null)
                    {
                        foreach (var s in sInfos)
                        {
                            try
                            {
                                var t = s.GetType().GetProperty("Type")?.GetValue(s, null) as Type;
                                if (t != null) serviceInfos.Add(t.FullName);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            SendJson(resp, 200, new
            {
                status = "ok",
                blockName,
                requestedLanguage = lang.ToString(),
                blockType = createdFb.GetType().FullName,
                attributeCount = attrs.Count,
                attributes = attrs,
                compositions,
                services = serviceInfos
            });
        }

        // ProDiag FB 의 Supervisions composition 에 Supervision 추가 시도 (검증용)
        // POST /api/plc/prodiag/add-supervision-test?plc=&block=&name=
        static void AddSupervisionTest(HttpListenerResponse resp, PlcSoftware plc, string blockName, string supervisionName)
        {
            if (string.IsNullOrEmpty(blockName))
            {
                SendJson(resp, 400, new { error = "Missing ?block= parameter" });
                return;
            }
            if (string.IsNullOrEmpty(supervisionName)) supervisionName = "Supv_" + DateTime.Now.ToString("HHmmss");

            // 블록 찾기 (재귀 검색)
            PlcBlock targetBlock = FindBlockByNameRecursive(plc.BlockGroup, blockName);
            if (targetBlock == null)
            {
                SendJson(resp, 404, new { error = "Block not found: " + blockName });
                return;
            }
            if (!(targetBlock is Siemens.Engineering.SW.Blocks.FB fb))
            {
                SendJson(resp, 400, new { error = "Block is not an FB: " + blockName, type = targetBlock.GetType().FullName });
                return;
            }

            var ieo = (IEngineeringObject)fb;
            var attempts = new List<Dictionary<string, object>>();
            object createdSupervision = null;

            // 시도 1: IEngineeringObject.Create("Supervisions", typeof(Supervision), null)
            try
            {
                var supType = typeof(Siemens.Engineering.SW.Blocks.Supervision);
                createdSupervision = ieo.Create("Supervisions", supType, null);
                attempts.Add(new Dictionary<string, object> {
                    { "method", "IEngineeringObject.Create(\"Supervisions\", typeof(Supervision), null)" },
                    { "status", createdSupervision != null ? "ok" : "returned_null" }
                });
            }
            catch (Exception ex)
            {
                attempts.Add(new Dictionary<string, object> {
                    { "method", "IEngineeringObject.Create(\"Supervisions\", typeof(Supervision), null)" },
                    { "status", "error" },
                    { "error", ex.Message },
                    { "innerError", ex.InnerException?.Message }
                });
            }

            // 시도 2: name 파라미터로 시도
            if (createdSupervision == null)
            {
                try
                {
                    var supType = typeof(Siemens.Engineering.SW.Blocks.Supervision);
                    var pars = new List<KeyValuePair<string, object>> {
                        new KeyValuePair<string, object>("Name", supervisionName)
                    };
                    createdSupervision = ieo.Create("Supervisions", supType, pars);
                    attempts.Add(new Dictionary<string, object> {
                        { "method", "IEngineeringObject.Create with Name parameter" },
                        { "status", createdSupervision != null ? "ok" : "returned_null" }
                    });
                }
                catch (Exception ex)
                {
                    attempts.Add(new Dictionary<string, object> {
                        { "method", "IEngineeringObject.Create with Name parameter" },
                        { "status", "error" },
                        { "error", ex.Message },
                        { "innerError", ex.InnerException?.Message }
                    });
                }
            }

            // 시도 3: GetCreationInfos 로 사용 가능한 composition 확인
            var creationInfos = new List<object>();
            try
            {
                var infos = ieo.GetType().GetMethod("GetCreationInfos", new[] { typeof(string) })?.Invoke(ieo, new object[] { "Supervisions" });
                if (infos is System.Collections.IEnumerable enumInfos)
                {
                    foreach (var info in enumInfos)
                    {
                        var name = info.GetType().GetProperty("Name")?.GetValue(info, null) as string;
                        var type = info.GetType().GetProperty("Type")?.GetValue(info, null) as Type;
                        creationInfos.Add(new { name, type = type?.FullName });
                    }
                }
            }
            catch { }

            // 생성된 Supervision dump
            object supervisionDump = null;
            if (createdSupervision != null)
            {
                var supIeo = createdSupervision as IEngineeringObject;
                if (supIeo != null)
                {
                    var supAttrs = ReadAllAttributesOf(supIeo);
                    var supComps = new List<object>();
                    try
                    {
                        foreach (var ci in supIeo.GetCompositionInfos())
                        {
                            string cn = ci.GetType().GetProperty("Name")?.GetValue(ci, null) as string;
                            if (!string.IsNullOrEmpty(cn)) supComps.Add(new { name = cn });
                        }
                    }
                    catch { }
                    supervisionDump = new {
                        type = createdSupervision.GetType().FullName,
                        attributeCount = supAttrs.Count,
                        attributes = supAttrs,
                        compositions = supComps
                    };
                }
            }

            // FB 의 Supervisions count 재확인
            int supervisionsCount = -1;
            try { supervisionsCount = fb.Supervisions.Count; } catch { }

            SendJson(resp, 200, new
            {
                blockName,
                blockType = fb.GetType().FullName,
                supervisionName,
                supervisionsCount,
                attempts,
                createdSupervision = supervisionDump,
                creationInfosForSupervisions = creationInfos
            });
        }

        // PlcBlockGroup 트리에서 이름으로 PlcBlock 찾기 (재귀)
        static PlcBlock FindBlockByNameRecursive(PlcBlockGroup group, string name)
        {
            foreach (PlcBlock b in group.Blocks)
            {
                if (b.Name == name) return b;
            }
            foreach (PlcBlockGroup sub in group.Groups)
            {
                var found = FindBlockByNameRecursive(sub, name);
                if (found != null) return found;
            }
            return null;
        }

        // CompilerResult.Messages 재귀 walk — 각 message 의 자식 Messages 까지 펼침
        static void WalkCompilerMessages(object msg, string parentPath, List<object> output, int maxItems)
        {
            if (output.Count >= maxItems) return;
            string path = SafeStr(() => (string)msg.GetType().GetProperty("Path")?.GetValue(msg));
            string desc = SafeStr(() => (string)msg.GetType().GetProperty("Description")?.GetValue(msg));
            string st = SafeStr(() => msg.GetType().GetProperty("State")?.GetValue(msg)?.ToString());
            string fullPath = string.IsNullOrEmpty(parentPath) ? (path ?? "") : (parentPath + " > " + (path ?? ""));
            if (!string.IsNullOrEmpty(desc) || !string.IsNullOrEmpty(path))
                output.Add(new { state = st, path = fullPath, description = desc });

            try
            {
                var subProp = msg.GetType().GetProperty("Messages");
                if (subProp != null)
                {
                    var sub = subProp.GetValue(msg, null) as System.Collections.IEnumerable;
                    if (sub != null)
                        foreach (var child in sub)
                        {
                            WalkCompilerMessages(child, fullPath, output, maxItems);
                            if (output.Count >= maxItems) return;
                        }
                }
            }
            catch { }
        }

        // PLC 컴파일 헬퍼 (Inconsistent block/UDT 자동 해결용). 결과 요약 반환.
        static object TryCompilePlcBeforeExport(PlcSoftware plc)
        {
            try
            {
                var compilable = ((IEngineeringServiceProvider)plc).GetService<ICompilable>();
                if (compilable == null) return new { state = "NotAvailable", note = "ICompilable service null" };
                var cr = compilable.Compile();
                Console.WriteLine("Pre-export compile: " + cr.State + " (errors=" + cr.ErrorCount + ", warnings=" + cr.WarningCount + ")");
                var messages = new List<object>();
                try
                {
                    foreach (var m in cr.Messages)
                        WalkCompilerMessages(m, "", messages, 50); // 최대 50개까지 재귀
                }
                catch { }
                return new {
                    state = cr.State.ToString(),
                    errorCount = cr.ErrorCount,
                    warningCount = cr.WarningCount,
                    messages
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine("Pre-export compile error: " + ex.Message);
                return new { state = "Exception", error = ex.Message };
            }
        }

        // ── Technology Objects (Motion Control 등) ──
        // PlcSoftware.TechnologicalObjectGroup 의 system group + user group 트리 walk

        // GET /api/plc/techno-objects?plc=
        static void GetTechnoObjects(HttpListenerResponse resp, PlcSoftware plc)
        {
            var root = plc.TechnologicalObjectGroup;
            if (root == null) { SendJson(resp, 200, new { count = 0, technoObjects = new object[0], note = "PlcSoftware.TechnologicalObjectGroup is null" }); return; }

            var result = new List<object>();
            CollectTechnoObjects(root, "", result);
            SendJson(resp, 200, new { count = result.Count, technoObjects = result });
        }

        static void CollectTechnoObjects(object group, string currentPath, List<object> result)
        {
            // TechnologicalInstanceDBs (이 그룹의 TO들)
            try
            {
                var tosProp = group.GetType().GetProperty("TechnologicalObjects");
                if (tosProp != null)
                {
                    var tos = tosProp.GetValue(group, null) as System.Collections.IEnumerable;
                    if (tos != null)
                    {
                        foreach (TechnologicalInstanceDB to in tos)
                        {
                            string libElem = null, libVer = null, isConsistent = null;
                            try { libElem = to.OfSystemLibElement; } catch { }
                            try { var v = to.OfSystemLibVersion; if (v != null) libVer = v.ToString(); } catch { }
                            try { isConsistent = ((IEngineeringObject)to).GetAttribute("IsConsistent")?.ToString(); } catch { }
                            result.Add(new {
                                name = to.Name,
                                group = currentPath,
                                ofSystemLibElement = libElem,
                                ofSystemLibVersion = libVer,
                                isConsistent
                            });
                        }
                    }
                }
            }
            catch { }

            // Groups (재귀 — user group composition)
            try
            {
                var groupsProp = group.GetType().GetProperty("Groups");
                if (groupsProp != null)
                {
                    var groups = groupsProp.GetValue(group, null) as System.Collections.IEnumerable;
                    if (groups != null)
                    {
                        foreach (var sub in groups)
                        {
                            string subName = null;
                            try { subName = sub.GetType().GetProperty("Name")?.GetValue(sub, null) as string; } catch { }
                            string subPath = string.IsNullOrEmpty(currentPath) ? (subName ?? "") : (currentPath + "/" + subName);
                            CollectTechnoObjects(sub, subPath, result);
                        }
                    }
                }
            }
            catch { }
        }

        // 재귀 search — TO 이름으로 찾기. 찾으면 그 TO 반환 + 부모 composition 도 같이.
        static TechnologicalInstanceDB FindTechnoObjectByName(object group, string toName)
        {
            try
            {
                var tosProp = group.GetType().GetProperty("TechnologicalObjects");
                if (tosProp != null)
                {
                    var tos = tosProp.GetValue(group, null) as System.Collections.IEnumerable;
                    if (tos != null)
                    {
                        foreach (TechnologicalInstanceDB to in tos)
                        {
                            if (to.Name == toName) return to;
                        }
                    }
                }
            }
            catch { }
            try
            {
                var groupsProp = group.GetType().GetProperty("Groups");
                if (groupsProp != null)
                {
                    var groups = groupsProp.GetValue(group, null) as System.Collections.IEnumerable;
                    if (groups != null)
                    {
                        foreach (var sub in groups)
                        {
                            var found = FindTechnoObjectByName(sub, toName);
                            if (found != null) return found;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        // GET /api/plc/techno-object/export?plc=&name=&compile=true|false
        static void ExportTechnoObject(HttpListenerResponse resp, PlcSoftware plc, string toName, bool compileFirst)
        {
            if (string.IsNullOrEmpty(toName)) { SendJson(resp, 400, new { error = "Missing ?name=" }); return; }
            var root = plc.TechnologicalObjectGroup;
            var to = FindTechnoObjectByName(root, toName);
            if (to == null) { SendJson(resp, 404, new { error = "Technology Object not found: " + toName }); return; }

            // 컴파일 옵션 — "Inconsistent blocks ... cannot be exported" 자동 해결
            object compileResult = compileFirst ? TryCompilePlcBeforeExport(plc) : null;

            // TO 의 IsConsistent 확인 (디버그용)
            string isConsistent = null;
            try { isConsistent = ((IEngineeringObject)to).GetAttribute("IsConsistent")?.ToString(); } catch { }

            Directory.CreateDirectory(tempDir);
            string fileName = MakeValidFileName(toName) + ".xml";
            string filePath = Path.Combine(tempDir, fileName);
            if (File.Exists(filePath)) File.Delete(filePath);

            try
            {
                to.Export(new FileInfo(filePath), ExportOptions.WithDefaults);
            }
            catch (Exception ex)
            {
                SendJson(resp, 500, new {
                    error = "Export failed: " + ex.Message,
                    innerError = ex.InnerException?.Message,
                    isConsistent,
                    compileResult,
                    hint = compileFirst
                        ? "컴파일은 실행됐지만 TO 가 여전히 inconsistent. compileResult.messages 에서 어떤 에러로 인해 inconsistent 인지 확인. 대개 TO 의 hardware connection (Drive, Encoder, Data exchange) 이 미설정. TIA Portal GUI 에서 TO config 채우고 정상 컴파일 통과시킨 후 export 가능."
                        : "Inconsistent 에러면 ?compile=true 로 재시도하세요 (export 전 자동 컴파일)"
                });
                return;
            }

            if (!File.Exists(filePath)) { SendJson(resp, 500, new { error = "Export 호출은 성공했지만 파일이 생성되지 않음" }); return; }

            SendFile(resp, filePath, fileName);
        }

        // GET /api/plc/techno-objects/export/zip?plc=&compile=true|false
        static void ExportTechnoObjectsZip(HttpListenerResponse resp, PlcSoftware plc, bool compileFirst)
        {
            var root = plc.TechnologicalObjectGroup;
            if (root == null) { SendJson(resp, 404, new { error = "TechnologicalObjectGroup null" }); return; }

            // 컴파일 옵션 — "Inconsistent blocks ... cannot be exported" 자동 해결
            if (compileFirst) TryCompilePlcBeforeExport(plc);

            Directory.CreateDirectory(tempDir);
            string zipPath = Path.Combine(tempDir, "techno_objects_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);

            var results = new List<object>();
            string exportDir = Path.Combine(tempDir, "to_export_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(exportDir);

            try
            {
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    ExportTechnoObjectsXmlToZip(root, "", zip, exportDir, results);
                }
            }
            catch (Exception ex)
            {
                SendJson(resp, 500, new { error = "ZIP export 실패: " + ex.Message });
                return;
            }
            finally
            {
                try { Directory.Delete(exportDir, true); } catch { }
            }

            SendFile(resp, zipPath, Path.GetFileName(zipPath));
        }

        static void ExportTechnoObjectsXmlToZip(object group, string currentPath, ZipArchive zip, string exportDir, List<object> results)
        {
            // TOs in this group
            try
            {
                var tosProp = group.GetType().GetProperty("TechnologicalObjects");
                if (tosProp != null)
                {
                    var tos = tosProp.GetValue(group, null) as System.Collections.IEnumerable;
                    if (tos != null)
                    {
                        foreach (TechnologicalInstanceDB to in tos)
                        {
                            string fileName = MakeValidFileName(to.Name) + ".xml";
                            string tempFile = Path.Combine(exportDir, fileName);
                            try
                            {
                                if (File.Exists(tempFile)) File.Delete(tempFile);
                                to.Export(new FileInfo(tempFile), ExportOptions.WithDefaults);
                                if (File.Exists(tempFile))
                                {
                                    string entryPath = string.IsNullOrEmpty(currentPath) ? fileName : currentPath + "/" + fileName;
                                    zip.CreateEntryFromFile(tempFile, entryPath);
                                    File.Delete(tempFile);
                                    results.Add(new { file = fileName, path = currentPath, status = "ok" });
                                }
                            }
                            catch (Exception ex)
                            {
                                results.Add(new { file = fileName, path = currentPath, status = "error", error = ex.Message });
                            }
                        }
                    }
                }
            }
            catch { }

            // Sub-groups
            try
            {
                var groupsProp = group.GetType().GetProperty("Groups");
                if (groupsProp != null)
                {
                    var groups = groupsProp.GetValue(group, null) as System.Collections.IEnumerable;
                    if (groups != null)
                    {
                        foreach (var sub in groups)
                        {
                            string subName = null;
                            try { subName = sub.GetType().GetProperty("Name")?.GetValue(sub, null) as string; } catch { }
                            string subPath = string.IsNullOrEmpty(currentPath) ? (subName ?? "") : (currentPath + "/" + subName);
                            ExportTechnoObjectsXmlToZip(sub, subPath, zip, exportDir, results);
                        }
                    }
                }
            }
            catch { }
        }

        // POST /api/plc/techno-object/import?plc=&group=<userGroupPath>&mode=Override|None
        // body = XML file (multipart 또는 raw)
        static void ImportTechnoObjectUpload(HttpListenerContext ctx, PlcSoftware plc, string groupPath, string modeStr)
        {
            ImportOptions mode = ImportOptions.Override;
            if (!string.IsNullOrEmpty(modeStr))
            {
                try { mode = (ImportOptions)Enum.Parse(typeof(ImportOptions), modeStr, true); }
                catch (Exception ex) { SendJson(ctx.Response, 400, new { error = "Invalid mode: " + modeStr + " (" + ex.Message + ")", validValues = Enum.GetNames(typeof(ImportOptions)) }); return; }
            }
            var root = plc.TechnologicalObjectGroup;
            if (root == null) { SendJson(ctx.Response, 404, new { error = "TechnologicalObjectGroup null" }); return; }

            // 그룹 결정 (system group 또는 user group). 미지정 시 system group 의 TOs
            object targetGroup = root;
            if (!string.IsNullOrEmpty(groupPath))
            {
                var parts = groupPath.Split('/');
                object cur = root;
                foreach (var p in parts)
                {
                    object next = null;
                    try
                    {
                        var groupsProp = cur.GetType().GetProperty("Groups");
                        if (groupsProp != null)
                        {
                            var groups = groupsProp.GetValue(cur, null) as System.Collections.IEnumerable;
                            if (groups != null)
                            {
                                foreach (var g in groups)
                                {
                                    string n = null;
                                    try { n = g.GetType().GetProperty("Name")?.GetValue(g, null) as string; } catch { }
                                    if (n == p) { next = g; break; }
                                }
                            }
                        }
                    }
                    catch { }
                    if (next == null) { SendJson(ctx.Response, 404, new { error = "User group not found: " + groupPath }); return; }
                    cur = next;
                }
                targetGroup = cur;
            }

            // composition 얻기
            object composition = null;
            try { composition = targetGroup.GetType().GetProperty("TechnologicalObjects")?.GetValue(targetGroup, null); } catch { }
            if (composition == null) { SendJson(ctx.Response, 500, new { error = "TechnologicalInstanceDBs composition null" }); return; }

            // body → temp file
            Directory.CreateDirectory(tempDir);
            string tempFile = Path.Combine(tempDir, "to_import_" + DateTime.Now.ToString("HHmmss_fff") + ".xml");
            try
            {
                using (var fs = File.Create(tempFile))
                {
                    ctx.Request.InputStream.CopyTo(fs);
                }
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Body 저장 실패: " + ex.Message });
                return;
            }
            if (new FileInfo(tempFile).Length == 0)
            {
                File.Delete(tempFile);
                SendJson(ctx.Response, 400, new { error = "Empty body" });
                return;
            }

            // Import (Composition.Import(FileInfo, ImportOptions))
            var imported = new List<object>();
            string importError = null;
            try
            {
                var importMethod = composition.GetType().GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) });
                if (importMethod == null) throw new Exception("Import method not found on TechnologicalInstanceDBComposition");
                var result = importMethod.Invoke(composition, new object[] { new FileInfo(tempFile), mode });
                if (result is System.Collections.IEnumerable list)
                {
                    foreach (TechnologicalInstanceDB to in list)
                    {
                        imported.Add(new { name = to.Name, ofSystemLibElement = SafeStr(() => to.OfSystemLibElement) });
                    }
                }
            }
            catch (Exception ex)
            {
                importError = ex.Message + (ex.InnerException != null ? " (inner: " + ex.InnerException.Message + ")" : "");
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }

            if (importError != null)
            {
                SendJson(ctx.Response, 500, new { error = "Import 실패: " + importError, mode = mode.ToString() });
                return;
            }
            SendJson(ctx.Response, 200, new { status = "ok", importedCount = imported.Count, imported, targetGroup = groupPath ?? "(system root)", mode = mode.ToString() });
        }

        static string SafeStr(Func<string> f) { try { return f(); } catch { return null; } }

        // GET /api/plc/techno-object/create-from-mastercopy?plc=&mc=<libPath/mcName>&group=
        static void CreateTechnoObjectFromMasterCopy(HttpListenerResponse resp, PlcSoftware plc, string mcPath, string groupPath)
        {
            if (string.IsNullOrEmpty(mcPath)) { SendJson(resp, 400, new { error = "Missing ?mc= (예: folder/mcName)" }); return; }
            var root = plc.TechnologicalObjectGroup;
            if (root == null) { SendJson(resp, 404, new { error = "TechnologicalObjectGroup null" }); return; }

            // MasterCopy 찾기
            Project project = null;
            foreach (Project p in connectedPortal.Projects) { project = p; break; }
            if (project == null) { SendJson(resp, 500, new { error = "No project" }); return; }

            MasterCopy mc = null;
            try
            {
                MasterCopyFolder folder = project.ProjectLibrary.MasterCopyFolder;
                var parts = mcPath.Split('/');
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    folder = folder.Folders.Find(parts[i]);
                    if (folder == null) break;
                }
                if (folder != null) mc = folder.MasterCopies.Find(parts[parts.Length - 1]);
            }
            catch { }
            if (mc == null) { SendJson(resp, 404, new { error = "MasterCopy not found: " + mcPath }); return; }

            // Target group/composition (위 ImportTechnoObjectUpload 와 동일 패턴)
            object targetGroup = root;
            if (!string.IsNullOrEmpty(groupPath))
            {
                var parts = groupPath.Split('/');
                object cur = root;
                foreach (var pName in parts)
                {
                    object next = null;
                    try
                    {
                        var groups = cur.GetType().GetProperty("Groups")?.GetValue(cur, null) as System.Collections.IEnumerable;
                        if (groups != null) foreach (var g in groups)
                        {
                            string n = null; try { n = g.GetType().GetProperty("Name")?.GetValue(g, null) as string; } catch { }
                            if (n == pName) { next = g; break; }
                        }
                    }
                    catch { }
                    if (next == null) { SendJson(resp, 404, new { error = "User group not found: " + groupPath }); return; }
                    cur = next;
                }
                targetGroup = cur;
            }

            object composition = null;
            try { composition = targetGroup.GetType().GetProperty("TechnologicalObjects")?.GetValue(targetGroup, null); } catch { }
            if (composition == null) { SendJson(resp, 500, new { error = "TechnologicalObjects composition null" }); return; }

            try
            {
                var createFromMethod = composition.GetType().GetMethod("CreateFrom", new[] { typeof(MasterCopy) });
                if (createFromMethod == null) { SendJson(resp, 500, new { error = "CreateFrom(MasterCopy) method not found" }); return; }
                var created = createFromMethod.Invoke(composition, new object[] { mc }) as TechnologicalInstanceDB;
                if (created == null) { SendJson(resp, 500, new { error = "CreateFrom returned null" }); return; }
                SendJson(resp, 200, new { status = "ok", createdName = created.Name, sourceMasterCopy = mcPath, targetGroup = groupPath ?? "(system root)" });
            }
            catch (Exception ex)
            {
                SendJson(resp, 500, new { error = "CreateFrom 실패: " + ex.Message, innerError = ex.InnerException?.Message });
            }
        }

        static void CreateUnit(HttpListenerResponse resp, PlcSoftware plc, string unitName, string unitNamespace)
        {
            if (string.IsNullOrEmpty(unitName))
            {
                SendJson(resp, 400, new { error = "Missing ?name= parameter" });
                return;
            }
            var unitProvider = plc.GetService<PlcUnitProvider>();
            if (unitProvider == null)
            {
                SendJson(resp, 500, new { error = "PlcUnitProvider not available" });
                return;
            }
            Console.WriteLine("Creating software unit: " + unitName + (string.IsNullOrEmpty(unitNamespace) ? "" : " (namespace: " + unitNamespace + ")"));

            var unit = unitProvider.UnitGroup.Units.Create(unitName);

            string appliedNamespace = null;
            string namespaceError = null;
            if (!string.IsNullOrEmpty(unitNamespace))
            {
                try
                {
                    ((IEngineeringObject)unit).SetAttribute("NamespacePreset", unitNamespace);
                }
                catch (Exception ex)
                {
                    namespaceError = ex.Message;
                }
            }
            try { appliedNamespace = ((IEngineeringObject)unit).GetAttribute("NamespacePreset") as string; } catch { }

            SendJson(resp, 200, new {
                message = "Unit created",
                name = unit.Name,
                @namespace = appliedNamespace,
                namespaceError = namespaceError
            });
        }

        // ── XML Import (single file or ZIP matching export folder structure) ──

        static void ImportXml(HttpListenerResponse resp, PlcSoftware plc, string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                SendJson(resp, 400, new { error = "Missing ?file= parameter (path to .xml or .zip file)" });
                return;
            }
            if (!File.Exists(filePath))
            {
                SendJson(resp, 404, new { error = "File not found: " + filePath });
                return;
            }

            if (filePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ImportXmlZip(resp, plc, filePath);
            }
            else
            {
                ImportSingleXmlAuto(resp, plc, filePath);
            }
        }

        static void ImportSingleXmlAuto(HttpListenerResponse resp, PlcSoftware plc, string filePath)
        {
            string content = "";
            try { content = File.ReadAllText(filePath, Encoding.UTF8).Substring(0, Math.Min(2000, (int)new FileInfo(filePath).Length)); } catch { }

            bool isType = content.Contains("<SW.Types.PlcStruct") || content.Contains("<SW.Types.");
            bool isTagTable = content.Contains("<SW.Tags.PlcTagTable") || content.Contains("<SW.Tags.");

            string fileName = Path.GetFileName(filePath);
            string shortPath = CopyToFlat(filePath, @"C:\T\import");

            try
            {
                if (isTagTable)
                {
                    plc.TagTableGroup.TagTables.Import(new FileInfo(shortPath), ImportOptions.Override);
                    SendJson(resp, 200, new { message = "Tag table imported", file = fileName, kind = "tagTable" });
                }
                else if (isType)
                {
                    plc.TypeGroup.Types.Import(new FileInfo(shortPath), ImportOptions.Override);
                    SendJson(resp, 200, new { message = "Type imported", file = fileName, kind = "type" });
                }
                else
                {
                    plc.BlockGroup.Blocks.Import(new FileInfo(shortPath), ImportOptions.Override);
                    SendJson(resp, 200, new { message = "Block imported", file = fileName, kind = "block" });
                }
            }
            catch (Exception ex)
            {
                SendJson(resp, 500, new { error = ex.Message, file = fileName, type = ex.GetType().Name });
            }
            finally { if (File.Exists(shortPath)) File.Delete(shortPath); }
        }

        // ── XML Import via file upload ──

        static void ImportUpload(HttpListenerContext ctx, PlcSoftware plc)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                SendJson(ctx.Response, 405, new { error = "POST required" });
                return;
            }

            string target = (ctx.Request.QueryString["target"] ?? "blocks").ToLower();   // blocks | tags | types
            string unit = ctx.Request.QueryString["unit"];                                // software unit name (optional)

            // Read uploaded XML body
            byte[] body;
            using (var ms = new MemoryStream())
            {
                ctx.Request.InputStream.CopyTo(ms);
                body = ms.ToArray();
            }
            if (body.Length == 0)
            {
                SendJson(ctx.Response, 400, new { error = "Empty request body. POST the XML file content." });
                return;
            }

            // Save to temp file (Openness API requires FileInfo)
            string tempFile = Path.Combine(tempDir, "upload_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".xml");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(tempFile, body);

            // Resolve import target groups
            PlcBlockGroup blockGroup = plc.BlockGroup;
            PlcTypeSystemGroup typeGroup = plc.TypeGroup;
            PlcTagTableSystemGroup tagGroup = plc.TagTableGroup;
            string resolvedUnit = null;

            if (!string.IsNullOrEmpty(unit))
            {
                var unitProvider = plc.GetService<PlcUnitProvider>();
                if (unitProvider == null)
                {
                    File.Delete(tempFile);
                    SendJson(ctx.Response, 500, new { error = "PlcUnitProvider not available" });
                    return;
                }
                bool found = false;
                foreach (PlcUnit pu in unitProvider.UnitGroup.Units)
                {
                    if (pu.Name == unit)
                    {
                        blockGroup = pu.BlockGroup;
                        typeGroup = pu.TypeGroup;
                        tagGroup = pu.TagTableGroup;
                        resolvedUnit = pu.Name;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    File.Delete(tempFile);
                    SendJson(ctx.Response, 404, new { error = "Software unit not found: " + unit });
                    return;
                }
            }

            // Copy to short path (Openness has path length limits)
            string shortPath = CopyToFlat(tempFile, @"C:\T\import");
            File.Delete(tempFile);

            try
            {
                string fileName = Path.GetFileName(shortPath);
                if (target == "tags")
                {
                    tagGroup.TagTables.Import(new FileInfo(shortPath), ImportOptions.Override);
                    SendJson(ctx.Response, 200, new { message = "Tag table imported", kind = "tags", unit = resolvedUnit });
                }
                else if (target == "types")
                {
                    typeGroup.Types.Import(new FileInfo(shortPath), ImportOptions.Override);
                    SendJson(ctx.Response, 200, new { message = "Type imported", kind = "types", unit = resolvedUnit });
                }
                else
                {
                    blockGroup.Blocks.Import(new FileInfo(shortPath), ImportOptions.Override);
                    SendJson(ctx.Response, 200, new { message = "Block imported", kind = "blocks", unit = resolvedUnit });
                }
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = ex.Message, type = ex.GetType().Name, target, unit = resolvedUnit });
            }
            finally { if (File.Exists(shortPath)) File.Delete(shortPath); }
        }

        /// <summary>
        /// Import from ZIP matching export folder structure:
        ///   PLCName/Program blocks/...
        ///   PLCName/PLC data types/...
        ///   PLCName/PLC tags/...
        ///   PLCName/Software units/UnitName/Program blocks/...
        ///   PLCName/Software units/UnitName/PLC data types/...
        ///   PLCName/Software units/UnitName/PLC tags/...
        /// </summary>
        static void ImportXmlZip(HttpListenerResponse resp, PlcSoftware plc, string zipPath)
        {
            var unitProvider = plc.GetService<PlcUnitProvider>();
            string shortBase = @"C:\T\" + DateTime.Now.ToString("HHmmss");
            string extractDir = shortBase + @"\z";
            string flatDir = shortBase + @"\f";
            Directory.CreateDirectory(extractDir);
            Directory.CreateDirectory(flatDir);

            var results = new List<object>();
            int success = 0, failed = 0;

            try
            {
                ZipFile.ExtractToDirectory(zipPath, extractDir);

                // Find root PLC folder (first level in ZIP)
                string[] topDirs = Directory.GetDirectories(extractDir);
                string rootDir = topDirs.Length == 1 ? topDirs[0] : extractDir;

                // ── Phase 1: Root PLC data types (import first for dependency) ──
                string typesDir = Path.Combine(rootDir, "PLC data types");
                if (Directory.Exists(typesDir))
                {
                    Console.WriteLine("=== Importing PLC data types ===");
                    ImportTypesFromXmlDir(typesDir, plc.TypeGroup, null, flatDir, results, ref success, ref failed);
                }

                // ── Phase 2: Root PLC tags ──
                string tagsDir = Path.Combine(rootDir, "PLC tags");
                if (Directory.Exists(tagsDir))
                {
                    Console.WriteLine("=== Importing PLC tags ===");
                    ImportTagTablesFromXmlDir(tagsDir, plc.TagTableGroup, null, flatDir, results, ref success, ref failed);
                }

                // ── Phase 3: Root Program blocks ──
                string blocksDir = Path.Combine(rootDir, "Program blocks");
                if (Directory.Exists(blocksDir))
                {
                    Console.WriteLine("=== Importing Program blocks ===");
                    ImportBlocksFromXmlDir(blocksDir, plc.BlockGroup, null, flatDir, results, ref success, ref failed);
                }

                // ── Phase 4: Software units ──
                // Create units and import ALL types first (cross-unit dependency),
                // then ALL tags, then ALL blocks
                string unitsDir = Path.Combine(rootDir, "Software units");
                var unitMap = new List<KeyValuePair<string, PlcUnit>>();
                if (Directory.Exists(unitsDir) && unitProvider != null)
                {
                    Console.WriteLine("=== Importing Software units ===");
                    foreach (string unitDir in Directory.GetDirectories(unitsDir))
                    {
                        string unitName = Path.GetFileName(unitDir);
                        bool isSafety = unitName.StartsWith("safety_");
                        string actualName = isSafety ? unitName.Substring("safety_".Length) : unitName;

                        PlcUnit targetUnit = null;
                        foreach (PlcUnit u in unitProvider.UnitGroup.Units)
                        {
                            if (u.Name == actualName) { targetUnit = u; break; }
                        }
                        if (targetUnit == null && !isSafety)
                        {
                            try { targetUnit = unitProvider.UnitGroup.Units.Create(actualName); Console.WriteLine("  Created unit: " + actualName); }
                            catch (Exception ex)
                            {
                                results.Add(new { unit = actualName, status = "error", error = "Failed to create unit: " + ex.Message });
                                failed++; continue;
                            }
                        }
                        if (targetUnit == null) { results.Add(new { unit = unitName, status = "skipped", error = "Unit not found" }); continue; }
                        unitMap.Add(new KeyValuePair<string, PlcUnit>(unitDir, targetUnit));
                    }

                    // Phase 4a: All unit tags (before types — types may use tag constants in array bounds)
                    Console.WriteLine("=== Phase 4a: All unit tags ===");
                    foreach (var kv in unitMap)
                    {
                        string uTagsDir = Path.Combine(kv.Key, "PLC tags");
                        if (Directory.Exists(uTagsDir))
                        {
                            Console.WriteLine("--- Tags: " + kv.Value.Name + " ---");
                            ImportTagTablesFromXmlDir(uTagsDir, kv.Value.TagTableGroup, kv.Value.Name, flatDir, results, ref success, ref failed);
                        }
                    }

                    // Phase 4b: All unit types (tag constants now available)
                    Console.WriteLine("=== Phase 4b: All unit types ===");
                    foreach (var kv in unitMap)
                    {
                        string uTypesDir = Path.Combine(kv.Key, "PLC data types");
                        if (Directory.Exists(uTypesDir))
                        {
                            Console.WriteLine("--- Types: " + kv.Value.Name + " ---");
                            ImportTypesFromXmlDir(uTypesDir, kv.Value.TypeGroup, kv.Value.Name, flatDir, results, ref success, ref failed);
                        }
                    }

                    // Phase 4c: All unit blocks
                    Console.WriteLine("=== Phase 4c: All unit blocks ===");
                    foreach (var kv in unitMap)
                    {
                        string uBlocksDir = Path.Combine(kv.Key, "Program blocks");
                        if (Directory.Exists(uBlocksDir))
                        {
                            Console.WriteLine("--- Blocks: " + kv.Value.Name + " ---");
                            ImportBlocksFromXmlDir(uBlocksDir, kv.Value.BlockGroup, kv.Value.Name, flatDir, results, ref success, ref failed);
                        }
                    }
                }

                SendJson(resp, 200, new { message = "XML Import complete", success, failed, details = results });
            }
            finally
            {
                if (Directory.Exists(shortBase)) Directory.Delete(shortBase, true);
            }
        }

        static void ImportTypesFromXmlDir(string dir, PlcTypeSystemGroup typeGroup, string unitName, string flatDir,
            List<object> results, ref int success, ref int failed)
        {
            var pending = Directory.GetFiles(dir, "*.xml", SearchOption.AllDirectories).ToList();
            int maxRounds = 20;

            for (int round = 1; round <= maxRounds && pending.Count > 0; round++)
            {
                var failedThisRound = new List<string>();
                int successBefore = success;

                Console.WriteLine("  Types round " + round + ": " + pending.Count + " files");
                foreach (string xmlFile in pending)
                {
                    string fileName = Path.GetFileName(xmlFile);
                    string shortPath = CopyToFlat(xmlFile, flatDir);
                    try
                    {
                        typeGroup.Types.Import(new FileInfo(shortPath), ImportOptions.Override);
                        Console.WriteLine("  Type OK: " + fileName);
                        results.Add(new { file = fileName, unit = unitName, kind = "type", status = "ok" });
                        success++;
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message.Contains("already exists"))
                        {
                            Console.WriteLine("  Type SKIPPED (exists): " + fileName);
                            results.Add(new { file = fileName, unit = unitName, kind = "type", status = "skipped", error = "Already exists in CPU" });
                            success++;
                        }
                        else
                        {
                            Console.WriteLine("  Type RETRY: " + fileName);
                            failedThisRound.Add(xmlFile);
                        }
                    }
                    finally { if (File.Exists(shortPath)) File.Delete(shortPath); }
                }

                // No progress this round — record remaining as errors and stop
                if (success == successBefore)
                {
                    foreach (string xmlFile in failedThisRound)
                    {
                        string fileName = Path.GetFileName(xmlFile);
                        string shortPath = CopyToFlat(xmlFile, flatDir);
                        try
                        {
                            typeGroup.Types.Import(new FileInfo(shortPath), ImportOptions.Override);
                            results.Add(new { file = fileName, unit = unitName, kind = "type", status = "ok" });
                            success++;
                        }
                        catch (Exception ex)
                        {
                            if (ex.Message.Contains("already exists"))
                            {
                                Console.WriteLine("  Type SKIPPED (exists): " + fileName);
                                results.Add(new { file = fileName, unit = unitName, kind = "type", status = "skipped", error = "Already exists in CPU" });
                                success++;
                            }
                            else
                            {
                                Console.WriteLine("  Type FAILED: " + fileName + " - " + ex.Message);
                                results.Add(new { file = fileName, unit = unitName, kind = "type", status = "error", error = ex.Message });
                                failed++;
                            }
                        }
                        finally { if (File.Exists(shortPath)) File.Delete(shortPath); }
                    }
                    break;
                }

                pending = failedThisRound;
            }
        }

        static void ImportTagTablesFromXmlDir(string dir, PlcTagTableSystemGroup tagGroup, string unitName, string flatDir,
            List<object> results, ref int success, ref int failed)
        {
            foreach (string xmlFile in Directory.GetFiles(dir, "*.xml", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileName(xmlFile);
                string shortPath = CopyToFlat(xmlFile, flatDir);
                Console.WriteLine("  TagTable: " + fileName + (unitName != null ? " -> " + unitName : " -> root"));
                try
                {
                    tagGroup.TagTables.Import(new FileInfo(shortPath), ImportOptions.Override);
                    results.Add(new { file = fileName, unit = unitName, kind = "tagTable", status = "ok" });
                    success++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  FAILED: " + ex.Message);
                    results.Add(new { file = fileName, unit = unitName, kind = "tagTable", status = "error", error = ex.Message });
                    failed++;
                }
                finally { if (File.Exists(shortPath)) File.Delete(shortPath); }
            }
        }

        static void ImportBlocksFromXmlDir(string dir, PlcBlockGroup blockGroup, string unitName, string flatDir,
            List<object> results, ref int success, ref int failed)
        {
            // Import order: non-instance blocks first, then DB/instance DBs
            var pending = Directory.GetFiles(dir, "*.xml", SearchOption.AllDirectories)
                .OrderBy(f =>
                {
                    string fn = f.ToLower();
                    if (fn.Contains("instance db")) return 2;
                    if (fn.EndsWith("_db.xml")) return 1;
                    return 0;
                })
                .ToList();

            int maxRounds = 20;

            for (int round = 1; round <= maxRounds && pending.Count > 0; round++)
            {
                var failedThisRound = new List<string>();
                int successBefore = success;

                Console.WriteLine("  Blocks round " + round + ": " + pending.Count + " files");
                foreach (string xmlFile in pending)
                {
                    string fileName = Path.GetFileName(xmlFile);
                    string shortPath = CopyToFlat(xmlFile, flatDir);

                    string relDir = Path.GetDirectoryName(xmlFile);
                    string relativePath = relDir.Length > dir.Length ? relDir.Substring(dir.Length).TrimStart('\\', '/') : "";

                    PlcBlockGroup targetGroup = NavigateOrCreateBlockGroup(blockGroup, relativePath);

                    try
                    {
                        targetGroup.Blocks.Import(new FileInfo(shortPath), ImportOptions.Override);
                        Console.WriteLine("  Block OK: " + fileName);
                        results.Add(new { file = fileName, unit = unitName, path = relativePath, kind = "block", status = "ok" });
                        success++;
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message.Contains("at least one compile unit") || ex.Message.Contains("already exists"))
                        {
                            string skipReason = ex.Message.Contains("already exists") ? "Already exists in CPU" : "Library block (no compile unit)";
                            Console.WriteLine("  Block SKIPPED: " + fileName + " - " + skipReason);
                            results.Add(new { file = fileName, unit = unitName, path = relativePath, kind = "block", status = "skipped", error = skipReason });
                            success++;
                        }
                        else
                        {
                            Console.WriteLine("  Block RETRY: " + fileName);
                            failedThisRound.Add(xmlFile);
                        }
                    }
                    finally { if (File.Exists(shortPath)) File.Delete(shortPath); }
                }

                // No progress — record remaining as errors
                if (success == successBefore)
                {
                    foreach (string xmlFile in failedThisRound)
                    {
                        string fileName = Path.GetFileName(xmlFile);
                        string shortPath = CopyToFlat(xmlFile, flatDir);
                        string relDir = Path.GetDirectoryName(xmlFile);
                        string relativePath = relDir.Length > dir.Length ? relDir.Substring(dir.Length).TrimStart('\\', '/') : "";
                        PlcBlockGroup targetGroup = NavigateOrCreateBlockGroup(blockGroup, relativePath);

                        try
                        {
                            targetGroup.Blocks.Import(new FileInfo(shortPath), ImportOptions.Override);
                            results.Add(new { file = fileName, unit = unitName, path = relativePath, kind = "block", status = "ok" });
                            success++;
                        }
                        catch (Exception ex)
                        {
                            if (ex.Message.Contains("at least one compile unit") || ex.Message.Contains("already exists"))
                            {
                                string skipReason = ex.Message.Contains("already exists") ? "Already exists in CPU" : "Library block (no compile unit)";
                                Console.WriteLine("  Block SKIPPED: " + fileName + " - " + skipReason);
                                results.Add(new { file = fileName, unit = unitName, path = relativePath, kind = "block", status = "skipped", error = skipReason });
                                success++;
                            }
                            else
                            {
                                Console.WriteLine("  Block FAILED: " + fileName + " - " + ex.Message);
                                results.Add(new { file = fileName, unit = unitName, path = relativePath, kind = "block", status = "error", error = ex.Message });
                                failed++;
                            }
                        }
                        finally { if (File.Exists(shortPath)) File.Delete(shortPath); }
                    }
                    break;
                }

                pending = failedThisRound;
            }
        }

        static PlcBlockGroup NavigateOrCreateBlockGroup(PlcBlockGroup root, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return root;

            PlcBlockGroup current = root;
            string[] parts = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                PlcBlockGroup found = null;
                foreach (PlcBlockGroup g in current.Groups)
                {
                    if (g.Name == part) { found = g; break; }
                }
                if (found == null)
                {
                    try { found = current.Groups.Create(part); Console.WriteLine("  Created group: " + part); }
                    catch { break; }
                }
                current = found;
            }
            return current;
        }

        // ── XML Export (all block types including LAD/FBD) ──

        // ProgrammingLanguage 기반 DB 판별 (DB / F_DB / CPU_DB / Motion_DB 모두 데이터 블록으로 취급)
        static bool IsDbBlock(PlcBlock b)
        {
            try
            {
                var pl = b.ProgrammingLanguage;
                return pl == ProgrammingLanguage.DB
                    || pl == ProgrammingLanguage.F_DB
                    || pl == ProgrammingLanguage.CPU_DB
                    || pl == ProgrammingLanguage.Motion_DB;
            }
            catch { return false; }
        }

        static void ExportBlocksXmlToZip(PlcBlockGroup group, string currentPath, string zipPrefix, ZipArchive zip, string exportDir, List<Dictionary<string, object>> results = null, Func<PlcBlock, bool> filter = null, Func<string, bool> pathFilter = null)
        {
            // currentPath is empty for the root group (skip root group name since prefix already has it)
            foreach (PlcBlock block in group.Blocks)
            {
                if (filter != null && !filter(block)) continue;
                string fileName = MakeValidFileName(block.Name) + ".xml";
                // pathFilter 는 선택 export 용. Export() 는 비싸므로 '쓰기 전' 에 거른다.
                string plannedPath = string.IsNullOrEmpty(currentPath)
                    ? zipPrefix + fileName
                    : zipPrefix + currentPath + "/" + fileName;
                if (pathFilter != null && !pathFilter(plannedPath)) continue;
                string tempFile = Path.Combine(exportDir, fileName);
                try
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                    block.Export(new FileInfo(tempFile), ExportOptions.WithDefaults);
                    if (File.Exists(tempFile))
                    {
                        string entryPath = string.IsNullOrEmpty(currentPath)
                            ? zipPrefix + fileName
                            : zipPrefix + currentPath + "/" + fileName;
                        zip.CreateEntryFromFile(tempFile, entryPath);
                        File.Delete(tempFile);
                        Console.WriteLine("  XML: " + entryPath);
                        if (results != null) results.Add(new Dictionary<string, object> { {"file", fileName}, {"path", currentPath}, {"kind", "block"}, {"status", "ok"} });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  XML export error [" + block.Name + "]: " + ex.Message);
                    if (results != null) results.Add(new Dictionary<string, object> { {"file", fileName}, {"path", currentPath}, {"kind", "block"}, {"status", "error"}, {"error", ex.Message} });
                }
            }
            foreach (PlcBlockGroup sub in group.Groups)
            {
                string subPath = string.IsNullOrEmpty(currentPath) ? sub.Name : currentPath + "/" + sub.Name;
                ExportBlocksXmlToZip(sub, subPath, zipPrefix, zip, exportDir, results, filter, pathFilter);
            }
        }

        static void ExportTypesXmlToZip(PlcTypeSystemGroup typeGroup, string zipPrefix, ZipArchive zip, string exportDir, List<Dictionary<string, object>> results = null, Func<string, bool> pathFilter = null)
        {
            ExportTypeGroupXmlToZip(typeGroup.Types, typeGroup.Groups, "", zipPrefix, zip, exportDir, results, pathFilter);
        }

        static void ExportTypeGroupXmlToZip(PlcTypeComposition types, PlcTypeUserGroupComposition groups, string groupPath, string zipPrefix, ZipArchive zip, string exportDir, List<Dictionary<string, object>> results = null, Func<string, bool> pathFilter = null)
        {
            foreach (PlcType t in types)
            {
                string fileName = MakeValidFileName(t.Name) + ".xml";
                if (pathFilter != null && !pathFilter(zipPrefix + groupPath + fileName)) continue;
                string tempFile = Path.Combine(exportDir, fileName);
                try
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                    t.Export(new FileInfo(tempFile), ExportOptions.WithDefaults);
                    if (File.Exists(tempFile))
                    {
                        string entryPath = zipPrefix + groupPath + fileName;
                        zip.CreateEntryFromFile(tempFile, entryPath);
                        File.Delete(tempFile);
                        Console.WriteLine("  XML: " + entryPath);
                        if (results != null) results.Add(new Dictionary<string, object> { {"file", fileName}, {"path", groupPath.TrimEnd('/')}, {"kind", "type"}, {"status", "ok"} });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  XML type export error [" + t.Name + "]: " + ex.Message);
                    if (results != null) results.Add(new Dictionary<string, object> { {"file", fileName}, {"path", groupPath.TrimEnd('/')}, {"kind", "type"}, {"status", "error"}, {"error", ex.Message} });
                }
            }
            foreach (PlcTypeUserGroup sub in groups)
            {
                string subPath = groupPath + MakeValidFileName(sub.Name) + "/";
                ExportTypeGroupXmlToZip(sub.Types, sub.Groups, subPath, zipPrefix, zip, exportDir, results, pathFilter);
            }
        }

        static void ExportUnitXmlZip(HttpListenerResponse resp, PlcSoftware plc, string unitName)
        {
            var unitProvider = plc.GetService<PlcUnitProvider>();
            if (unitProvider == null) { SendJson(resp, 404, new { error = "No software units" }); return; }

            PlcBlockGroup blockGroup = null;
            PlcTypeSystemGroup typeGroup = null;
            PlcTagTableSystemGroup tagGroup = null;

            foreach (PlcUnit unit in unitProvider.UnitGroup.Units)
            {
                if (unit.Name == unitName) { blockGroup = unit.BlockGroup; typeGroup = unit.TypeGroup; tagGroup = unit.TagTableGroup; break; }
            }
            if (blockGroup == null)
            {
                foreach (PlcSafetyUnit su in unitProvider.UnitGroup.SafetyUnits)
                {
                    if (su.Name == unitName) { blockGroup = su.BlockGroup; typeGroup = su.TypeGroup; tagGroup = su.TagTableGroup; break; }
                }
            }
            if (blockGroup == null) { SendJson(resp, 404, new { error = "Unit not found: " + unitName }); return; }

            string exportDir = Path.Combine(tempDir, Guid.NewGuid().ToString().Substring(0, 8));
            Directory.CreateDirectory(exportDir);
            string zipFile = Path.Combine(tempDir, MakeValidFileName(unitName) + "_xml.zip");
            if (File.Exists(zipFile)) File.Delete(zipFile);

            Console.WriteLine("XML exporting unit: " + unitName);
            try
            {
                using (var zip = ZipFile.Open(zipFile, ZipArchiveMode.Create))
                {
                    string prefix = MakeValidFileName(unitName) + "/";
                    ExportBlocksXmlToZip(blockGroup, "", prefix, zip, exportDir);
                    ExportTypesXmlToZip(typeGroup, prefix, zip, exportDir);
                    ExportTagTablesToZip(tagGroup.TagTables, prefix, zip, exportDir, tagGroup.Groups);
                }
                SendFile(resp, zipFile, MakeValidFileName(unitName) + "_xml.zip");
            }
            finally
            {
                if (File.Exists(zipFile)) File.Delete(zipFile);
                if (Directory.Exists(exportDir)) Directory.Delete(exportDir, true);
            }
        }

        static void ExportAllXmlZip(HttpListenerResponse resp, PlcSoftware plc, string root = "all", bool compileFirst = false)
        {
            if (root != "all" && root != "program_blocks" && root != "software_units")
            {
                SendJson(resp, 400, new { error = "Invalid root parameter. Use: all, program_blocks, software_units" });
                return;
            }

            string plcName = MakeValidFileName(plc.Name);
            string exportDir = Path.Combine(tempDir, Guid.NewGuid().ToString().Substring(0, 8));
            Directory.CreateDirectory(exportDir);
            string zipFile = Path.Combine(tempDir, plcName + "_xml_" + root + ".zip");
            if (File.Exists(zipFile)) File.Delete(zipFile);

            // ?compile=true: export 전에 software compile 한 번 실행
            // ("Inconsistent blocks and PLC data types (UDT) cannot be exported" 에러 자동 해결 시도)
            object preCompileSummary = null;
            if (compileFirst)
            {
                try
                {
                    var compilable = ((IEngineeringServiceProvider)plc).GetService<ICompilable>();
                    if (compilable != null)
                    {
                        var cr = compilable.Compile();
                        preCompileSummary = new
                        {
                            state = cr.State.ToString(),
                            errorCount = cr.ErrorCount,
                            warningCount = cr.WarningCount
                        };
                        Console.WriteLine("Pre-export compile: " + cr.State + " (errors=" + cr.ErrorCount + ", warnings=" + cr.WarningCount + ")");
                    }
                }
                catch (Exception ex)
                {
                    preCompileSummary = new { error = "Pre-export compile 실패: " + ex.Message };
                    Console.WriteLine("Pre-export compile error: " + ex.Message);
                }
            }

            Console.WriteLine("XML exporting from PLC: " + plc.Name + " (root=" + root + ")");
            var results = new List<Dictionary<string, object>>();
            try
            {
                using (var zip = ZipFile.Open(zipFile, ZipArchiveMode.Create))
                {
                    string plcPrefix = plcName + "/";

                    // 새 export 순서:
                    //   1) PLC data types  (UDT 정의 — DB / 코드 블록 모두 의존)
                    //   2) DB blocks       (데이터 블록 먼저 — 코드 블록이 참조)
                    //   3) PLC tags        (태그 테이블)
                    //   4) FB / FC / OB    (코드 블록 — 위 셋을 모두 참조)
                    if (root == "all" || root == "program_blocks")
                    {
                        // 1. Types
                        ExportTypesXmlToZip(plc.TypeGroup, plcPrefix + "PLC data types/", zip, exportDir, results);

                        // 2. DB blocks
                        ExportBlocksXmlToZip(plc.BlockGroup, "", plcPrefix + "Program blocks/", zip, exportDir, results, IsDbBlock);

                        // 3. PLC tags
                        ExportTagTablesToZip(plc.TagTableGroup.TagTables, plcPrefix + "PLC tags/", zip, exportDir, plc.TagTableGroup.Groups, results);

                        // 4. Non-DB blocks (FB/FC/OB)
                        ExportBlocksXmlToZip(plc.BlockGroup, "", plcPrefix + "Program blocks/", zip, exportDir, results, b => !IsDbBlock(b));
                    }

                    // Software units — 같은 새 순서로 unit 별 처리
                    if (root == "all" || root == "software_units")
                    {
                        var unitProvider = plc.GetService<PlcUnitProvider>();
                        if (unitProvider != null)
                        {
                            string suBase = plcPrefix + "Software units/";
                            foreach (PlcUnit unit in unitProvider.UnitGroup.Units)
                            {
                                string unitPrefix = suBase + MakeValidFileName(unit.Name) + "/";
                                Console.WriteLine("  XML exporting unit: " + unit.Name);
                                ExportTypesXmlToZip(unit.TypeGroup, unitPrefix + "PLC data types/", zip, exportDir, results);
                                ExportBlocksXmlToZip(unit.BlockGroup, "", unitPrefix + "Program blocks/", zip, exportDir, results, IsDbBlock);
                                ExportTagTablesToZip(unit.TagTableGroup.TagTables, unitPrefix + "PLC tags/", zip, exportDir, unit.TagTableGroup.Groups, results);
                                ExportBlocksXmlToZip(unit.BlockGroup, "", unitPrefix + "Program blocks/", zip, exportDir, results, b => !IsDbBlock(b));
                            }
                            foreach (PlcSafetyUnit su in unitProvider.UnitGroup.SafetyUnits)
                            {
                                string unitPrefix = suBase + "safety_" + MakeValidFileName(su.Name) + "/";
                                Console.WriteLine("  XML exporting safety unit: " + su.Name);
                                ExportTypesXmlToZip(su.TypeGroup, unitPrefix + "PLC data types/", zip, exportDir, results);
                                ExportBlocksXmlToZip(su.BlockGroup, "", unitPrefix + "Program blocks/", zip, exportDir, results, IsDbBlock);
                                ExportTagTablesToZip(su.TagTableGroup.TagTables, unitPrefix + "PLC tags/", zip, exportDir, su.TagTableGroup.Groups, results);
                                ExportBlocksXmlToZip(su.BlockGroup, "", unitPrefix + "Program blocks/", zip, exportDir, results, b => !IsDbBlock(b));
                            }
                        }
                    }

                    // Add export report inside ZIP
                    int okCount = results.Count(r => (string)r["status"] == "ok");
                    int errCount = results.Count(r => (string)r["status"] == "error");
                    var errorDetails = results.Where(r => (string)r["status"] == "error").ToList();
                    var report = new Dictionary<string, object> {
                        {"message", "XML Export complete"},
                        {"success", okCount},
                        {"failed", errCount},
                        {"details", errorDetails},
                        {"preCompile", preCompileSummary}
                    };
                    string reportJson = json.Serialize(report);
                    var reportEntry = zip.CreateEntry(plcName + "/_export_report.json");
                    using (var sw = new StreamWriter(reportEntry.Open())) { sw.Write(reportJson); }
                }

                int totalOk = results.Count(r => (string)r["status"] == "ok");
                int totalErr = results.Count(r => (string)r["status"] == "error");
                SendFileWithExportReport(resp, zipFile, plcName + "_xml_" + root + ".zip", totalOk, totalErr);
            }
            finally
            {
                if (File.Exists(zipFile)) File.Delete(zipFile);
                if (Directory.Exists(exportDir)) Directory.Delete(exportDir, true);
            }
        }

        // ══ CPU 트리 + 선택 Export ══════════════════════════════════════
        //
        // 선택 ID = ZIP 엔트리 경로다. /api/plc/tree 가 돌려주는 leaf 의 path 를
        // 그대로 /api/plc/export/selected 에 넘기면 그 XML 만 담긴 ZIP 이 온다.
        // 경로 생성 규칙은 ExportAllXmlZip 과 글자 단위로 같게 맞춰져 있다
        // (블록 폴더는 원본 이름, 타입/태그 폴더는 MakeValidFileName 적용).

        static Dictionary<string, object> TreeLeaf(string name, string kind, string path, object info)
        {
            var d = new Dictionary<string, object> { { "name", name }, { "kind", kind }, { "path", path } };
            if (info != null) d["info"] = info;
            return d;
        }

        static Dictionary<string, object> TreeFolder(string name, List<object> children)
        {
            return new Dictionary<string, object> { { "name", name }, { "kind", "folder" }, { "children", children } };
        }

        static List<object> TreeBlocks(PlcBlockGroup group, string currentPath, string zipPrefix)
        {
            var items = new List<object>();
            foreach (PlcBlock block in group.Blocks)
            {
                string entryPath = string.IsNullOrEmpty(currentPath)
                    ? zipPrefix + MakeValidFileName(block.Name) + ".xml"
                    : zipPrefix + currentPath + "/" + MakeValidFileName(block.Name) + ".xml";
                object info;
                try { info = new { type = block.GetType().Name, number = block.Number, consistent = block.IsConsistent }; }
                catch { info = new { type = block.GetType().Name }; }
                items.Add(TreeLeaf(block.Name, "block", entryPath, info));
            }
            foreach (PlcBlockGroup sub in group.Groups)
            {
                string subPath = string.IsNullOrEmpty(currentPath) ? sub.Name : currentPath + "/" + sub.Name;
                items.Add(TreeFolder(sub.Name, TreeBlocks(sub, subPath, zipPrefix)));
            }
            return items;
        }

        static List<object> TreeTypes(PlcTypeComposition types, PlcTypeUserGroupComposition groups, string groupPath, string zipPrefix)
        {
            var items = new List<object>();
            foreach (PlcType t in types)
                items.Add(TreeLeaf(t.Name, "type", zipPrefix + groupPath + MakeValidFileName(t.Name) + ".xml", null));
            foreach (PlcTypeUserGroup sub in groups)
                items.Add(TreeFolder(sub.Name, TreeTypes(sub.Types, sub.Groups, groupPath + MakeValidFileName(sub.Name) + "/", zipPrefix)));
            return items;
        }

        static List<object> TreeTags(PlcTagTableComposition tables, PlcTagTableUserGroupComposition groups, string groupPath, string zipPrefix)
        {
            var items = new List<object>();
            foreach (PlcTagTable t in tables)
                items.Add(TreeLeaf(t.Name, "tagTable", zipPrefix + groupPath + MakeValidFileName(t.Name) + ".xml", null));
            if (groups != null)
                foreach (PlcTagTableUserGroup sub in groups)
                    items.Add(TreeFolder(sub.Name, TreeTags(sub.TagTables, sub.Groups, groupPath + MakeValidFileName(sub.Name) + "/", zipPrefix)));
            return items;
        }

        static int CountLeaves(List<object> nodes)
        {
            int n = 0;
            foreach (object o in nodes)
            {
                var d = (Dictionary<string, object>)o;
                if ((string)d["kind"] == "folder") n += CountLeaves((List<object>)d["children"]);
                else n++;
            }
            return n;
        }

        static List<object> UnitTriple(string unitPrefix, PlcTypeSystemGroup tg, PlcBlockGroup bg, PlcTagTableSystemGroup tt)
        {
            return new List<object> {
                TreeFolder("PLC data types", TreeTypes(tg.Types, tg.Groups, "", unitPrefix + "PLC data types/")),
                TreeFolder("Program blocks", TreeBlocks(bg, "", unitPrefix + "Program blocks/")),
                TreeFolder("PLC tags", TreeTags(tt.TagTables, tt.Groups, "", unitPrefix + "PLC tags/"))
            };
        }

        // GET /api/plc/tree?plc=
        static void GetPlcTree(HttpListenerResponse resp, PlcSoftware plc)
        {
            string plcName = MakeValidFileName(plc.Name);
            string plcPrefix = plcName + "/";
            var roots = new List<object>();
            try
            {
                roots.Add(TreeFolder("PLC data types",
                    TreeTypes(plc.TypeGroup.Types, plc.TypeGroup.Groups, "", plcPrefix + "PLC data types/")));
                roots.Add(TreeFolder("Program blocks",
                    TreeBlocks(plc.BlockGroup, "", plcPrefix + "Program blocks/")));
                roots.Add(TreeFolder("PLC tags",
                    TreeTags(plc.TagTableGroup.TagTables, plc.TagTableGroup.Groups, "", plcPrefix + "PLC tags/")));

                var unitProvider = plc.GetService<PlcUnitProvider>();
                if (unitProvider != null)
                {
                    var units = new List<object>();
                    string suBase = plcPrefix + "Software units/";
                    foreach (PlcUnit unit in unitProvider.UnitGroup.Units)
                        units.Add(TreeFolder(unit.Name, UnitTriple(
                            suBase + MakeValidFileName(unit.Name) + "/", unit.TypeGroup, unit.BlockGroup, unit.TagTableGroup)));
                    foreach (PlcSafetyUnit su in unitProvider.UnitGroup.SafetyUnits)
                        units.Add(TreeFolder("safety_" + su.Name, UnitTriple(
                            suBase + "safety_" + MakeValidFileName(su.Name) + "/", su.TypeGroup, su.BlockGroup, su.TagTableGroup)));
                    if (units.Count > 0) roots.Add(TreeFolder("Software units", units));
                }
            }
            catch (Exception ex)
            {
                SendJson(resp, 500, new { error = "트리 조회 실패: " + ex.Message });
                return;
            }
            SendJson(resp, 200, new { plc = plc.Name, root = plcName, leafCount = CountLeaves(roots), tree = roots });
        }

        // POST /api/plc/export/selected?plc=[&compile=true]
        // body: {"paths":["PLC_1/Program blocks/Foo.xml", ...]}
        static void ExportSelectedXmlZip(HttpListenerContext ctx, PlcSoftware plc, bool compileFirst)
        {
            var resp = ctx.Response;
            string body;
            using (var ms = new MemoryStream())
            {
                ctx.Request.InputStream.CopyTo(ms);
                body = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }

            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var d = json.Deserialize<Dictionary<string, object>>(body);
                object arr;
                if (d != null && d.TryGetValue("paths", out arr) && arr is System.Collections.IEnumerable)
                    foreach (object o in (System.Collections.IEnumerable)arr)
                        if (o != null) selected.Add(o.ToString().Replace('\\', '/').Trim());
            }
            catch (Exception ex)
            {
                SendJson(resp, 400, new {
                    error = "본문 JSON 파싱 실패: " + ex.Message,
                    expected = "{\"paths\":[\"PLC_1/Program blocks/Foo.xml\"]}"
                });
                return;
            }
            if (selected.Count == 0)
            {
                SendJson(resp, 400, new {
                    error = "paths 가 비어 있습니다",
                    hint = "GET /api/plc/tree?plc=... 의 leaf path 를 그대로 넣으세요"
                });
                return;
            }

            string plcName = MakeValidFileName(plc.Name);
            string exportDir = Path.Combine(tempDir, Guid.NewGuid().ToString().Substring(0, 8));
            Directory.CreateDirectory(exportDir);
            string zipFile = Path.Combine(tempDir, plcName + "_xml_selected.zip");
            if (File.Exists(zipFile)) File.Delete(zipFile);

            object preCompileSummary = null;
            if (compileFirst)
            {
                try
                {
                    var compilable = ((IEngineeringServiceProvider)plc).GetService<ICompilable>();
                    if (compilable != null)
                    {
                        var cr = compilable.Compile();
                        preCompileSummary = new { state = cr.State.ToString(), errorCount = cr.ErrorCount, warningCount = cr.WarningCount };
                    }
                }
                catch (Exception ex) { preCompileSummary = new { error = "Pre-export compile 실패: " + ex.Message }; }
            }

            Console.WriteLine("XML exporting (selected " + selected.Count + ") from PLC: " + plc.Name);
            var results = new List<Dictionary<string, object>>();
            // ZipArchiveMode.Create 로 연 아카이브는 항목 열거가 금지돼 있다
            // (.NET: "만들기 모드에서는 항목에 액세스할 수 없습니다").
            // 그래서 무엇이 산출됐는지는 필터가 지나간 경로로 직접 기록한다.
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Func<string, bool> pf = pth =>
            {
                if (!selected.Contains(pth)) return false;
                matched.Add(pth);
                return true;
            };
            try
            {
                using (var zip = ZipFile.Open(zipFile, ZipArchiveMode.Create))
                {
                    string plcPrefix = plcName + "/";

                    // ExportAllXmlZip 과 같은 순서: 타입 → DB → 태그 → 코드블록
                    ExportTypesXmlToZip(plc.TypeGroup, plcPrefix + "PLC data types/", zip, exportDir, results, pf);
                    ExportBlocksXmlToZip(plc.BlockGroup, "", plcPrefix + "Program blocks/", zip, exportDir, results, IsDbBlock, pf);
                    ExportTagTablesToZip(plc.TagTableGroup.TagTables, plcPrefix + "PLC tags/", zip, exportDir, plc.TagTableGroup.Groups, results, pf);
                    ExportBlocksXmlToZip(plc.BlockGroup, "", plcPrefix + "Program blocks/", zip, exportDir, results, b => !IsDbBlock(b), pf);

                    var unitProvider = plc.GetService<PlcUnitProvider>();
                    if (unitProvider != null)
                    {
                        string suBase = plcPrefix + "Software units/";
                        foreach (PlcUnit unit in unitProvider.UnitGroup.Units)
                        {
                            string up = suBase + MakeValidFileName(unit.Name) + "/";
                            ExportTypesXmlToZip(unit.TypeGroup, up + "PLC data types/", zip, exportDir, results, pf);
                            ExportBlocksXmlToZip(unit.BlockGroup, "", up + "Program blocks/", zip, exportDir, results, IsDbBlock, pf);
                            ExportTagTablesToZip(unit.TagTableGroup.TagTables, up + "PLC tags/", zip, exportDir, unit.TagTableGroup.Groups, results, pf);
                            ExportBlocksXmlToZip(unit.BlockGroup, "", up + "Program blocks/", zip, exportDir, results, b => !IsDbBlock(b), pf);
                        }
                        foreach (PlcSafetyUnit su in unitProvider.UnitGroup.SafetyUnits)
                        {
                            string up = suBase + "safety_" + MakeValidFileName(su.Name) + "/";
                            ExportTypesXmlToZip(su.TypeGroup, up + "PLC data types/", zip, exportDir, results, pf);
                            ExportBlocksXmlToZip(su.BlockGroup, "", up + "Program blocks/", zip, exportDir, results, IsDbBlock, pf);
                            ExportTagTablesToZip(su.TagTableGroup.TagTables, up + "PLC tags/", zip, exportDir, su.TagTableGroup.Groups, results, pf);
                            ExportBlocksXmlToZip(su.BlockGroup, "", up + "Program blocks/", zip, exportDir, results, b => !IsDbBlock(b), pf);
                        }
                    }

                    // 요청했는데 트리 순회에서 만나지 못한 경로 — 이름 변경 또는 경로 오타
                    var notFound = selected.Where(x => !matched.Contains(x)).ToList();

                    int okCount = results.Count(r => (string)r["status"] == "ok");
                    int errCount = results.Count(r => (string)r["status"] == "error");
                    var report = new Dictionary<string, object> {
                        {"message", "XML Export complete (selected)"},
                        {"requested", selected.Count},
                        {"success", okCount},
                        {"failed", errCount},
                        {"notFound", notFound},
                        {"details", results.Where(r => (string)r["status"] == "error").ToList()},
                        {"preCompile", preCompileSummary}
                    };
                    var reportEntry = zip.CreateEntry(plcName + "/_export_report.json");
                    using (var sw = new StreamWriter(reportEntry.Open())) { sw.Write(json.Serialize(report)); }
                }

                int totalOk = results.Count(r => (string)r["status"] == "ok");
                int totalErr = results.Count(r => (string)r["status"] == "error");
                SendFileWithExportReport(resp, zipFile, plcName + "_xml_selected.zip", totalOk, totalErr);
            }
            finally
            {
                if (File.Exists(zipFile)) File.Delete(zipFile);
                if (Directory.Exists(exportDir)) Directory.Delete(exportDir, true);
            }
        }

        // ── XML Import ──

        static string CopyToFlat(string sourceFile, string flatDir)
        {
            if (!Directory.Exists(flatDir)) Directory.CreateDirectory(flatDir);
            string fileName = Path.GetFileName(sourceFile);
            string shortPath = Path.Combine(flatDir, fileName);
            int counter = 1;
            while (File.Exists(shortPath))
            {
                shortPath = Path.Combine(flatDir, Path.GetFileNameWithoutExtension(fileName) + "_" + counter + Path.GetExtension(fileName));
                counter++;
            }
            File.Copy(sourceFile, shortPath);
            return shortPath;
        }

        // ── Export / Download ──

        static string ExportBlockToFile(PlcBlock block, PlcExternalSourceSystemGroup extGroup, string outputDir)
        {
            string lang = block.ProgrammingLanguage.ToString();
            if (!langToExt.ContainsKey(lang)) return null;

            string ext = langToExt[lang];
            string fileName = MakeValidFileName(block.Name) + ext;
            string tempFile = Path.Combine(outputDir, fileName);
            var fileInfo = new FileInfo(tempFile);

            try
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
                extGroup.GenerateSource(new List<PlcBlock> { block }, fileInfo, GenerateOptions.None);
                return tempFile;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Export error [" + block.Name + "]: " + ex.Message);
                return null;
            }
        }

        static void ExportBlocksToZip(PlcBlockGroup group, PlcExternalSourceSystemGroup extGroup,
            string currentPath, string zipPrefix, ZipArchive zip, string exportDir)
        {
            string groupPath = string.IsNullOrEmpty(currentPath) ? group.Name : currentPath + "/" + group.Name;

            foreach (PlcBlock block in group.Blocks)
            {
                string lang = block.ProgrammingLanguage.ToString();
                if (!langToExt.ContainsKey(lang)) continue;

                string exported = ExportBlockToFile(block, extGroup, exportDir);
                if (exported != null && File.Exists(exported))
                {
                    string langFolder = lang.ToLower();
                    string entryPath = zipPrefix + langFolder + "/" + groupPath + "/" + MakeValidFileName(block.Name) + langToExt[lang];
                    zip.CreateEntryFromFile(exported, entryPath);
                    File.Delete(exported);
                    Console.WriteLine("  Zipped: " + entryPath);
                }
            }
            foreach (PlcBlockGroup sub in group.Groups)
            {
                ExportBlocksToZip(sub, extGroup, groupPath, zipPrefix, zip, exportDir);
            }
        }

        static void ExportTypesToZip(PlcTypeSystemGroup typeGroup, PlcExternalSourceSystemGroup extGroup,
            string zipPrefix, ZipArchive zip, string exportDir)
        {
            foreach (PlcType t in typeGroup.Types)
            {
                string fileName = t.Name + ".udt";
                string tempFile = Path.Combine(exportDir, fileName);
                var fileInfo = new FileInfo(tempFile);
                try
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                    extGroup.GenerateSource(new List<PlcType> { t }, fileInfo, GenerateOptions.None);
                    if (File.Exists(tempFile))
                    {
                        string entryPath = zipPrefix + "udt/" + fileName;
                        zip.CreateEntryFromFile(tempFile, entryPath);
                        File.Delete(tempFile);
                        Console.WriteLine("  Zipped: " + entryPath);
                    }
                }
                catch (Exception ex) { Console.WriteLine("Type export error [" + t.Name + "]: " + ex.Message); }
            }
        }

        static void ExportTagTablesToZip(PlcTagTableComposition tables, string zipPrefix, ZipArchive zip, string exportDir, PlcTagTableUserGroupComposition groups = null, List<Dictionary<string, object>> results = null, Func<string, bool> pathFilter = null)
        {
            ExportTagTableGroupToZip(tables, groups, "", zipPrefix, zip, exportDir, results, pathFilter);
        }

        static void ExportTagTableGroupToZip(PlcTagTableComposition tables, PlcTagTableUserGroupComposition groups, string groupPath, string zipPrefix, ZipArchive zip, string exportDir, List<Dictionary<string, object>> results = null, Func<string, bool> pathFilter = null)
        {
            foreach (PlcTagTable table in tables)
            {
                string fileName = MakeValidFileName(table.Name) + ".xml";
                if (pathFilter != null && !pathFilter(zipPrefix + groupPath + fileName)) continue;
                string tempFile = Path.Combine(exportDir, fileName);
                try
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                    table.Export(new FileInfo(tempFile), ExportOptions.WithDefaults);
                    if (File.Exists(tempFile))
                    {
                        string entryPath = zipPrefix + groupPath + fileName;
                        zip.CreateEntryFromFile(tempFile, entryPath);
                        File.Delete(tempFile);
                        Console.WriteLine("  Zipped: " + entryPath);
                        if (results != null) results.Add(new Dictionary<string, object> { {"file", fileName}, {"path", groupPath.TrimEnd('/')}, {"kind", "tagTable"}, {"status", "ok"} });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Tag export error [" + table.Name + "]: " + ex.Message);
                    if (results != null) results.Add(new Dictionary<string, object> { {"file", fileName}, {"path", groupPath.TrimEnd('/')}, {"kind", "tagTable"}, {"status", "error"}, {"error", ex.Message} });
                }
            }
            if (groups != null)
            {
                foreach (PlcTagTableUserGroup sub in groups)
                {
                    string subPath = groupPath + MakeValidFileName(sub.Name) + "/";
                    ExportTagTableGroupToZip(sub.TagTables, sub.Groups, subPath, zipPrefix, zip, exportDir, results, pathFilter);
                }
            }
        }

        static void ExportSingleBlock(HttpListenerResponse resp, PlcSoftware plc, string blockName, string unitName, string format = null)
        {
            if (string.IsNullOrEmpty(blockName))
            {
                SendJson(resp, 400, new { error = "Missing ?name= parameter" });
                return;
            }

            PlcBlock block = null;
            PlcExternalSourceSystemGroup extGroup = null;

            if (string.IsNullOrEmpty(unitName))
            {
                block = FindBlock(plc.BlockGroup, blockName);
                extGroup = plc.ExternalSourceGroup;
            }
            else
            {
                var unitProvider = plc.GetService<PlcUnitProvider>();
                if (unitProvider != null)
                {
                    foreach (PlcUnit unit in unitProvider.UnitGroup.Units)
                    {
                        if (unit.Name == unitName)
                        {
                            block = FindBlock(unit.BlockGroup, blockName);
                            extGroup = unit.ExternalSourceGroup;
                            break;
                        }
                    }
                    if (block == null)
                    {
                        foreach (PlcSafetyUnit su in unitProvider.UnitGroup.SafetyUnits)
                        {
                            if (su.Name == unitName)
                            {
                                block = FindBlock(su.BlockGroup, blockName);
                                extGroup = su.ExternalSourceGroup;
                                break;
                            }
                        }
                    }
                }
            }

            if (block == null) { SendJson(resp, 404, new { error = "Block not found: " + blockName }); return; }

            string lang = block.ProgrammingLanguage.ToString();
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

            // format 결정:
            //  - "source": 텍스트 source code (SCL/DB/STL) - GenerateSource 사용
            //  - "xml":    block.Export() 로 XML 강제 (graphical 언어 포함 모든 언어 지원)
            //  - 미지정:    텍스트 가능하면 source, 안 되면 자동으로 XML fallback
            string fmt = (format ?? "").ToLower();
            bool sourceSupported = langToExt.ContainsKey(lang);
            bool useXml = fmt == "xml" || (fmt != "source" && !sourceSupported);

            if (useXml)
            {
                string xmlFile = Path.Combine(tempDir, MakeValidFileName(block.Name) + ".xml");
                if (File.Exists(xmlFile)) File.Delete(xmlFile);
                try
                {
                    block.Export(new FileInfo(xmlFile), ExportOptions.WithDefaults);
                    if (!File.Exists(xmlFile))
                    {
                        SendJson(resp, 500, new { error = "XML export 가 파일을 생성하지 않았습니다: " + blockName });
                        return;
                    }
                    resp.Headers.Add("X-Block-Language", lang);
                    resp.Headers.Add("X-Export-Format", "xml");
                    resp.Headers.Add("Access-Control-Expose-Headers", "X-Block-Language, X-Export-Format");
                    SendFile(resp, xmlFile, MakeValidFileName(block.Name) + ".xml");
                }
                catch (Exception ex)
                {
                    SendJson(resp, 500, new { error = "XML export 실패: " + ex.Message, language = lang });
                }
                finally
                {
                    if (File.Exists(xmlFile)) try { File.Delete(xmlFile); } catch { }
                }
                return;
            }

            // source export (SCL / DB / STL)
            if (extGroup == null) { SendJson(resp, 500, new { error = "ExternalSourceGroup not available" }); return; }
            if (!sourceSupported)
            {
                SendJson(resp, 400, new
                {
                    error = "Language '" + lang + "' 는 source export 미지원. ?format=xml 로 XML export 사용",
                    language = lang,
                    supportedSourceLanguages = new[] { "SCL", "DB", "STL" },
                    hint = "graphical 언어 (LAD/FBD/GRAPH/CFC/SFC) 는 ?format=xml 또는 format 미지정 시 자동 XML fallback"
                });
                return;
            }

            string exported = ExportBlockToFile(block, extGroup, tempDir);
            if (exported == null || !File.Exists(exported))
            {
                SendJson(resp, 500, new { error = "Source export 실패: " + blockName });
                return;
            }

            resp.Headers.Add("X-Block-Language", lang);
            resp.Headers.Add("X-Export-Format", "source");
            resp.Headers.Add("Access-Control-Expose-Headers", "X-Block-Language, X-Export-Format");
            SendFile(resp, exported, MakeValidFileName(block.Name) + langToExt[lang]);
            File.Delete(exported);
        }

        static void ExportUnitZip(HttpListenerResponse resp, PlcSoftware plc, string unitName)
        {
            var unitProvider = plc.GetService<PlcUnitProvider>();
            if (unitProvider == null) { SendJson(resp, 404, new { error = "No software units" }); return; }

            PlcBlockGroup blockGroup = null;
            PlcExternalSourceSystemGroup extGroup = null;
            PlcTypeSystemGroup typeGroup = null;
            PlcTagTableSystemGroup tagGroup = null;

            foreach (PlcUnit unit in unitProvider.UnitGroup.Units)
            {
                if (unit.Name == unitName)
                {
                    blockGroup = unit.BlockGroup;
                    extGroup = unit.ExternalSourceGroup;
                    typeGroup = unit.TypeGroup;
                    tagGroup = unit.TagTableGroup;
                    break;
                }
            }
            if (blockGroup == null)
            {
                foreach (PlcSafetyUnit su in unitProvider.UnitGroup.SafetyUnits)
                {
                    if (su.Name == unitName)
                    {
                        blockGroup = su.BlockGroup;
                        extGroup = su.ExternalSourceGroup;
                        typeGroup = su.TypeGroup;
                        tagGroup = su.TagTableGroup;
                        break;
                    }
                }
            }
            if (blockGroup == null) { SendJson(resp, 404, new { error = "Unit not found: " + unitName }); return; }

            string exportDir = Path.Combine(tempDir, Guid.NewGuid().ToString());
            Directory.CreateDirectory(exportDir);
            string zipFile = Path.Combine(tempDir, MakeValidFileName(unitName) + ".zip");
            if (File.Exists(zipFile)) File.Delete(zipFile);

            Console.WriteLine("Exporting unit: " + unitName);
            try
            {
                using (var zip = ZipFile.Open(zipFile, ZipArchiveMode.Create))
                {
                    string prefix = MakeValidFileName(unitName) + "/";
                    ExportBlocksToZip(blockGroup, extGroup, "", prefix, zip, exportDir);
                    ExportTypesToZip(typeGroup, extGroup, prefix, zip, exportDir);
                    ExportTagTablesToZip(tagGroup.TagTables, prefix, zip, exportDir, tagGroup.Groups);
                }
                SendFile(resp, zipFile, MakeValidFileName(unitName) + ".zip");
            }
            finally
            {
                if (File.Exists(zipFile)) File.Delete(zipFile);
                if (Directory.Exists(exportDir)) Directory.Delete(exportDir, true);
            }
        }

        // ExportAllZip removed - use ExportAllXmlZip with root parameter instead

        static void SendFile(HttpListenerResponse resp, string filePath, string downloadName)
        {
            byte[] data = File.ReadAllBytes(filePath);
            resp.StatusCode = 200;
            if (downloadName.EndsWith(".zip"))
                resp.ContentType = "application/zip";
            else
                resp.ContentType = "application/octet-stream";
            resp.Headers.Add("Content-Disposition", "attachment; filename=\"" + downloadName + "\"");
            resp.ContentLength64 = data.Length;
            resp.OutputStream.Write(data, 0, data.Length);
            resp.Close();
        }

        static void SendFileWithExportReport(HttpListenerResponse resp, string filePath, string downloadName, int success, int failed)
        {
            byte[] data = File.ReadAllBytes(filePath);
            resp.StatusCode = 200;
            resp.ContentType = "application/zip";
            resp.Headers.Add("Content-Disposition", "attachment; filename=\"" + downloadName + "\"");
            resp.Headers.Add("X-Export-Success", success.ToString());
            resp.Headers.Add("X-Export-Failed", failed.ToString());
            resp.Headers.Add("Access-Control-Expose-Headers", "X-Export-Success, X-Export-Failed");
            resp.ContentLength64 = data.Length;
            resp.OutputStream.Write(data, 0, data.Length);
            resp.Close();
        }

        // ── HMI Routes ──

        static HmiTarget FindHmi(string deviceItemName)
        {
            EnsureConnected();
            foreach (Project proj in connectedPortal.Projects)
            {
                foreach (Device device in proj.Devices)
                {
                    // 1) 디바이스 이름이 일치하면 하위 전체를 탐색하여 HmiTarget을 찾는다
                    if (device.Name == deviceItemName)
                    {
                        var hmi = FindHmiInDeviceItems(device.DeviceItems);
                        if (hmi != null) return hmi;
                    }
                    // 2) DeviceItem 이름이 일치하면 해당 아이템 + 하위를 탐색
                    foreach (DeviceItem di in device.DeviceItems)
                    {
                        var hmi = FindHmiInDeviceItem(di, deviceItemName);
                        if (hmi != null) return hmi;
                    }
                }
            }
            return null;
        }

        static HmiTarget FindHmiInDeviceItem(DeviceItem di, string targetName)
        {
            if (di.Name == targetName)
            {
                var sc = ((IEngineeringServiceProvider)di).GetService<SoftwareContainer>();
                if (sc != null && sc.Software is HmiTarget hmi) return hmi;
            }
            // 하위 DeviceItem 재귀 탐색
            foreach (DeviceItem sub in di.DeviceItems)
            {
                var hmi = FindHmiInDeviceItem(sub, targetName);
                if (hmi != null) return hmi;
            }
            return null;
        }

        static HmiTarget FindHmiInDeviceItems(DeviceItemComposition items)
        {
            foreach (DeviceItem di in items)
            {
                var sc = ((IEngineeringServiceProvider)di).GetService<SoftwareContainer>();
                if (sc != null && sc.Software is HmiTarget hmi) return hmi;
                var found = FindHmiInDeviceItems(di.DeviceItems);
                if (found != null) return found;
            }
            return null;
        }

        // ── Unified HMI (HmiSoftware) ──

        // HmiSoftware 를 device 이름으로 찾는다. HmiTarget 과 별도 — Unified HMI 전용.
        static HmiSoftware FindHmiSoftware(string deviceItemName)
        {
            EnsureConnected();
            foreach (Project proj in connectedPortal.Projects)
            {
                foreach (Device device in proj.Devices)
                {
                    if (device.Name == deviceItemName)
                    {
                        var hmi = FindHmiSoftwareInDeviceItems(device.DeviceItems);
                        if (hmi != null) return hmi;
                    }
                    foreach (DeviceItem di in device.DeviceItems)
                    {
                        var hmi = FindHmiSoftwareInDeviceItem(di, deviceItemName);
                        if (hmi != null) return hmi;
                    }
                }
            }
            return null;
        }

        static HmiSoftware FindHmiSoftwareInDeviceItem(DeviceItem di, string targetName)
        {
            if (di.Name == targetName)
            {
                try
                {
                    var sc = ((IEngineeringServiceProvider)di).GetService<SoftwareContainer>();
                    if (sc != null && sc.Software is HmiSoftware hs) return hs;
                }
                catch { }
            }
            foreach (DeviceItem sub in di.DeviceItems)
            {
                var found = FindHmiSoftwareInDeviceItem(sub, targetName);
                if (found != null) return found;
            }
            return null;
        }

        static HmiSoftware FindHmiSoftwareInDeviceItems(DeviceItemComposition items)
        {
            foreach (DeviceItem di in items)
            {
                try
                {
                    var sc = ((IEngineeringServiceProvider)di).GetService<SoftwareContainer>();
                    if (sc != null && sc.Software is HmiSoftware hs) return hs;
                }
                catch { }
                var found = FindHmiSoftwareInDeviceItems(di.DeviceItems);
                if (found != null) return found;
            }
            return null;
        }

        // ── Unified HMI screens enumeration + MasterCopy cross-project 시도 ──

        // GET /api/hmi/screens?hmi=<unifiedName>  — Unified HMI 의 screens 트리 dump
        // V20 에 Export() 메서드 없음 → enumerate 만, attribute 는 메타 정도
        static void ListUnifiedHmiScreens(HttpListenerResponse resp, HmiSoftware hmi)
        {
            var rootScreens = new List<object>();
            try
            {
                foreach (var s in hmi.Screens)
                {
                    rootScreens.Add(new {
                        name = SafeAttr(s, "Name"),
                        path = SafeAttr(s, "Name")
                    });
                }
            }
            catch { }

            var groups = new List<object>();
            try
            {
                foreach (var g in hmi.ScreenGroups)
                {
                    groups.Add(WalkScreenGroup(g, ""));
                }
            }
            catch { }

            SendJson(resp, 200, new
            {
                hmi = ((IEngineeringObject)hmi).GetAttribute("Name"),
                kind = "HMI_Unified",
                rootScreensCount = rootScreens.Count,
                rootScreens,
                screenGroups = groups,
                note = "Unified HMI 는 V20 Openness 에서 Screen.Export() 미지원. 이 endpoint 는 enumerate 만 가능. cross-project 이전은 /api/hmi/screen/create-mastercopy 시도 (V20 한계로 round-trip 미보장)"
            });
        }

        static object WalkScreenGroup(object group, string parentPath)
        {
            string name = SafeAttr(group, "Name");
            string path = string.IsNullOrEmpty(parentPath) ? name : parentPath + "/" + name;
            var screens = new List<object>();
            try
            {
                var screensProp = group.GetType().GetProperty("Screens");
                if (screensProp != null)
                {
                    var sc = screensProp.GetValue(group, null) as System.Collections.IEnumerable;
                    if (sc != null)
                        foreach (var s in sc)
                            screens.Add(new { name = SafeAttr(s, "Name"), path = path + "/" + SafeAttr(s, "Name") });
                }
            }
            catch { }
            var subGroups = new List<object>();
            try
            {
                var groupsProp = group.GetType().GetProperty("Groups");
                if (groupsProp != null)
                {
                    var gs = groupsProp.GetValue(group, null) as System.Collections.IEnumerable;
                    if (gs != null)
                        foreach (var sub in gs)
                            subGroups.Add(WalkScreenGroup(sub, path));
                }
            }
            catch { }
            return new { name, path, screens, subGroups };
        }

        static string SafeAttr(object o, string name)
        {
            try { return ((IEngineeringObject)o).GetAttribute(name) as string; } catch { return null; }
        }

        // group tree 에서 path (slash-separated) 로 screen 찾기
        static object FindUnifiedScreenByPath(HmiSoftware hmi, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = path.Split('/');
            // root 단일 이름이면 hmi.Screens 에서 검색
            if (parts.Length == 1)
            {
                foreach (var s in hmi.Screens)
                {
                    if (SafeAttr(s, "Name") == parts[0]) return s;
                }
                // 또는 root 의 group 이름으로 시작 (parts[0]) 후 추가가 없음
            }
            // group 들 walk
            object currentGroup = null;
            int startIdx = 0;
            foreach (var g in hmi.ScreenGroups)
            {
                if (SafeAttr(g, "Name") == parts[0]) { currentGroup = g; startIdx = 1; break; }
            }
            if (currentGroup == null) return null;

            for (int i = startIdx; i < parts.Length - 1; i++)
            {
                object nextGroup = null;
                try
                {
                    var gs = currentGroup.GetType().GetProperty("Groups")?.GetValue(currentGroup, null) as System.Collections.IEnumerable;
                    if (gs != null) foreach (var g in gs) if (SafeAttr(g, "Name") == parts[i]) { nextGroup = g; break; }
                }
                catch { }
                if (nextGroup == null) return null;
                currentGroup = nextGroup;
            }

            // 마지막 segment 는 screen (또는 group 인 경우 그 자체 반환)
            string lastName = parts[parts.Length - 1];
            try
            {
                var sc = currentGroup.GetType().GetProperty("Screens")?.GetValue(currentGroup, null) as System.Collections.IEnumerable;
                if (sc != null) foreach (var s in sc) if (SafeAttr(s, "Name") == lastName) return s;
            }
            catch { }
            return null;
        }

        // GET /api/hmi/screen/create-mastercopy?hmi=&screen=<path>&libPath=
        // Unified HMI 의 screen 을 IMasterCopySource 로 캐스팅해서 MasterCopy 생성 시도
        static void CreateMasterCopyFromUnifiedScreen(HttpListenerContext ctx, HmiSoftware hmi)
        {
            string screenPath = ctx.Request.QueryString["screen"];
            string libFolderPath = ctx.Request.QueryString["libPath"] ?? "";
            if (string.IsNullOrEmpty(screenPath))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?screen= (예: 400_Alarm 또는 400_Alarm/401_Alarm History)" });
                return;
            }

            object screen = FindUnifiedScreenByPath(hmi, screenPath);
            if (screen == null)
            {
                SendJson(ctx.Response, 404, new { error = "Unified screen not found: " + screenPath, hint = "GET /api/hmi/screens?hmi=... 로 경로 확인" });
                return;
            }

            // IMasterCopySource 캐스트 가능 여부
            bool isMcSource = false;
            try { isMcSource = screen is IMasterCopySource; } catch { }

            if (!isMcSource)
            {
                SendJson(ctx.Response, 500, new
                {
                    error = "HmiScreen 이 IMasterCopySource 를 구현하지 않음 — V20 Openness 한계",
                    screenType = screen.GetType().FullName,
                    hint = "TIA Portal GUI 에서 우클릭 > Add to library 는 동작하나 Openness API 로는 노출 안 됨"
                });
                return;
            }

            // ProjectLibrary 의 target folder 찾기
            Project project = null;
            foreach (Project p in connectedPortal.Projects) { project = p; break; }
            if (project == null) { SendJson(ctx.Response, 500, new { error = "No project" }); return; }

            try
            {
                MasterCopyComposition mcTarget;
                if (string.IsNullOrEmpty(libFolderPath))
                {
                    mcTarget = project.ProjectLibrary.MasterCopyFolder.MasterCopies;
                }
                else
                {
                    var folder = NavigateOrCreateMasterCopyFolder(project.ProjectLibrary.MasterCopyFolder, libFolderPath);
                    mcTarget = folder.MasterCopies;
                }
                var newMc = mcTarget.Create((IMasterCopySource)screen);
                SendJson(ctx.Response, 200, new
                {
                    status = "ok",
                    message = "MasterCopy 생성 성공",
                    masterCopyName = newMc.Name,
                    sourceScreen = screenPath,
                    libraryFolder = libFolderPath,
                    note = "이제 GET /api/library/list 로 확인 가능. cross-project 사용은 (1) project library → global library 저장, (2) target project 에서 global library 열기, (3) target HMI 로 복원 시도 (V20 한계 가능성). 다음 단계: POST /api/hmi/screen/create-from-mastercopy"
                });
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new
                {
                    status = "create_failed",
                    error = ex.Message,
                    innerError = ex.InnerException?.Message,
                    screenType = screen.GetType().FullName,
                    hint = "캐스팅은 성공했지만 MasterCopy.Create() 가 거부함 — V20 Openness 가 Unified screen 을 library source 로 지원 안 함"
                });
            }
        }

        // POST /api/hmi/screen/create-from-mastercopy?hmi=&masterCopy=<libPath/mcName>&groupPath=
        // ProjectLibrary 의 MasterCopy 를 target Unified HMI 의 screen 으로 복원 시도 (V20 한계 검증용)
        static void CreateUnifiedScreenFromMasterCopy(HttpListenerContext ctx, HmiSoftware hmi)
        {
            string mcPath = ctx.Request.QueryString["masterCopy"];
            string groupPath = ctx.Request.QueryString["groupPath"] ?? "";
            string libraryName = ctx.Request.QueryString["library"]; // Global library 이름 (지정 시 그쪽에서 찾음)
            if (string.IsNullOrEmpty(mcPath))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?masterCopy= (예: folder/screenName)" });
                return;
            }

            Project project = null;
            foreach (Project p in connectedPortal.Projects) { project = p; break; }
            if (project == null) { SendJson(ctx.Response, 500, new { error = "No project" }); return; }

            // MasterCopyFolder 의 시작 위치 결정 — Project Library 또는 Global Library
            MasterCopyFolder rootFolder = null;
            string libSource = null;
            if (!string.IsNullOrEmpty(libraryName))
            {
                string resolvedSrc, resolvedName;
                List<string> available;
                var globalLib = ResolveGlobalLibraryByName(libraryName, out resolvedSrc, out resolvedName, out available);
                if (globalLib == null)
                {
                    SendJson(ctx.Response, 404, new {
                        error = "Global Library not found: " + libraryName,
                        available,
                        hint = "TIA Portal 에서 라이브러리를 열거나 /api/global-library/open 사용. 또는 ?library= 생략하면 Project Library 사용"
                    });
                    return;
                }
                try { rootFolder = globalLib.MasterCopyFolder; libSource = "GlobalLibrary[" + (resolvedName ?? libraryName) + "]"; }
                catch (Exception ex) { SendJson(ctx.Response, 500, new { error = "GlobalLibrary.MasterCopyFolder 접근 실패: " + ex.Message }); return; }
            }
            else
            {
                rootFolder = project.ProjectLibrary.MasterCopyFolder;
                libSource = "ProjectLibrary";
            }

            // MasterCopy 찾기 (folder/name)
            MasterCopy mc = null;
            try
            {
                MasterCopyFolder folder = rootFolder;
                var parts = mcPath.Split('/');
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    folder = folder.Folders.Find(parts[i]);
                    if (folder == null) break;
                }
                if (folder != null) mc = folder.MasterCopies.Find(parts[parts.Length - 1]);
            }
            catch { }
            if (mc == null)
            {
                SendJson(ctx.Response, 404, new {
                    error = "MasterCopy not found: " + mcPath,
                    library = libSource,
                    hint = "Global Library 안에 있다면 ?library=<라이브러리이름> 명시. 또는 /api/global-library/list 로 정확한 경로 확인"
                });
                return;
            }

            // 타겟 ScreenComposition 결정 (root 또는 group path)
            object targetComp = null;
            string resolvedTargetDescription = "";
            if (string.IsNullOrEmpty(groupPath))
            {
                targetComp = hmi.Screens;
                resolvedTargetDescription = "HmiSoftware.Screens (root)";
            }
            else
            {
                // groupPath 의 그룹 찾기
                object curGroup = null;
                var parts = groupPath.Split('/');
                foreach (var g in hmi.ScreenGroups) if (SafeAttr(g, "Name") == parts[0]) { curGroup = g; break; }
                for (int i = 1; i < parts.Length && curGroup != null; i++)
                {
                    object next = null;
                    var gs = curGroup.GetType().GetProperty("Groups")?.GetValue(curGroup, null) as System.Collections.IEnumerable;
                    if (gs != null) foreach (var g in gs) if (SafeAttr(g, "Name") == parts[i]) { next = g; break; }
                    curGroup = next;
                }
                if (curGroup == null)
                {
                    SendJson(ctx.Response, 404, new { error = "ScreenGroup not found: " + groupPath });
                    return;
                }
                targetComp = curGroup.GetType().GetProperty("Screens")?.GetValue(curGroup, null);
                resolvedTargetDescription = "HmiScreenGroup[" + groupPath + "].Screens";
            }
            if (targetComp == null) { SendJson(ctx.Response, 500, new { error = "Target screens composition is null" }); return; }

            // 여러 방식 시도
            var attempts = new List<Dictionary<string, object>>();

            // 시도 1: 직접 CreateFrom(MasterCopy) - 메서드 존재하면 호출
            try
            {
                var m = targetComp.GetType().GetMethod("CreateFrom", new[] { typeof(MasterCopy) });
                if (m != null)
                {
                    var created = m.Invoke(targetComp, new object[] { mc });
                    attempts.Add(new Dictionary<string, object> {
                        { "method", "HmiScreenComposition.CreateFrom(MasterCopy)" },
                        { "status", created != null ? "ok" : "returned_null" },
                        { "createdScreen", created != null ? SafeAttr(created, "Name") : null }
                    });
                    if (created != null)
                    {
                        SendJson(ctx.Response, 200, new
                        {
                            status = "ok",
                            method = "HmiScreenComposition.CreateFrom(MasterCopy)",
                            createdScreen = SafeAttr(created, "Name"),
                            targetComposition = resolvedTargetDescription,
                            attempts
                        });
                        return;
                    }
                }
                else
                {
                    attempts.Add(new Dictionary<string, object> {
                        { "method", "HmiScreenComposition.CreateFrom(MasterCopy)" },
                        { "status", "method_not_found" }
                    });
                }
            }
            catch (Exception ex)
            {
                attempts.Add(new Dictionary<string, object> {
                    { "method", "HmiScreenComposition.CreateFrom(MasterCopy)" },
                    { "status", "error" },
                    { "error", ex.Message },
                    { "innerError", ex.InnerException?.Message }
                });
            }

            // 시도 2: targetComp 을 IEngineeringObject 로 캐스팅 후 Create
            var ieoComp = targetComp as IEngineeringObject;
            attempts.Add(new Dictionary<string, object> {
                { "method", "targetComp as IEngineeringObject" },
                { "status", ieoComp != null ? "castable" : "not_castable" },
                { "targetCompType", targetComp.GetType().FullName }
            });

            if (ieoComp != null)
            {
                var screenType = Type.GetType("Siemens.Engineering.HmiUnified.UI.Screens.HmiScreen, Siemens.Engineering");
                if (screenType == null)
                {
                    try { screenType = typeof(HmiScreen); } catch { }
                }

                if (screenType != null)
                {
                    try
                    {
                        var pars = new List<KeyValuePair<string, object>> {
                            new KeyValuePair<string, object>("MasterCopy", mc)
                        };
                        var created = ieoComp.Create("Screens", screenType, pars);
                        attempts.Add(new Dictionary<string, object> {
                            { "method", "targetComp.Create('Screens', HmiScreen, MasterCopy=mc)" },
                            { "status", created != null ? "ok" : "returned_null" },
                            { "createdScreen", created != null ? SafeAttr(created, "Name") : null }
                        });
                        if (created != null)
                        {
                            SendJson(ctx.Response, 200, new { status = "ok", method = "targetComp.Create", createdScreen = SafeAttr(created, "Name"), attempts });
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        attempts.Add(new Dictionary<string, object> {
                            { "method", "targetComp.Create('Screens', HmiScreen, MasterCopy=mc)" },
                            { "status", "error" },
                            { "error", ex.Message },
                            { "innerError", ex.InnerException?.Message }
                        });
                    }
                }
                else
                {
                    attempts.Add(new Dictionary<string, object> {
                        { "method", "typeof HmiScreen" },
                        { "status", "type lookup failed" }
                    });
                }
            }

            // 시도 3: HmiSoftware 를 IEngineeringObject 로 캐스팅 후 Create("Screens", ...) — 부모 레벨 시도
            try
            {
                var ieoHmi = hmi as IEngineeringObject;
                if (ieoHmi != null)
                {
                    var screenType = typeof(HmiScreen);
                    var pars = new List<KeyValuePair<string, object>> {
                        new KeyValuePair<string, object>("MasterCopy", mc)
                    };
                    var created = ieoHmi.Create("Screens", screenType, pars);
                    attempts.Add(new Dictionary<string, object> {
                        { "method", "hmi.Create('Screens', HmiScreen, MasterCopy=mc)" },
                        { "status", created != null ? "ok" : "returned_null" },
                        { "createdScreen", created != null ? SafeAttr(created, "Name") : null }
                    });
                    if (created != null)
                    {
                        SendJson(ctx.Response, 200, new { status = "ok", method = "hmi.Create", createdScreen = SafeAttr(created, "Name"), attempts });
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                attempts.Add(new Dictionary<string, object> {
                    { "method", "hmi.Create('Screens', HmiScreen, MasterCopy=mc)" },
                    { "status", "error" },
                    { "error", ex.Message },
                    { "innerError", ex.InnerException?.Message }
                });
            }

            // 시도 4: GetCreationInfos 로 어떤 type 이 create 가능한지 dump (진단용)
            var creationInfos = new List<object>();
            try
            {
                var ieoHmi = hmi as IEngineeringObject;
                if (ieoHmi != null)
                {
                    var m = ieoHmi.GetType().GetMethod("GetCreationInfos", new[] { typeof(string) });
                    if (m != null)
                    {
                        var infos = m.Invoke(ieoHmi, new object[] { "Screens" }) as System.Collections.IEnumerable;
                        if (infos != null)
                        {
                            foreach (var info in infos)
                            {
                                var typeName = info.GetType().GetProperty("Type")?.GetValue(info, null)?.ToString();
                                creationInfos.Add(new { type = typeName });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { creationInfos.Add(new { error = ex.Message }); }

            SendJson(ctx.Response, 500, new
            {
                status = "all_attempts_failed",
                message = "Unified HMI 에 MasterCopy 로부터 screen 복원 — V20 Openness 미지원 확정",
                masterCopyName = mc.Name,
                masterCopyContentType = mc.ContentDescriptions.Count > 0 ? mc.ContentDescriptions[0].ContentType.Name : null,
                targetComposition = resolvedTargetDescription,
                attempts,
                creationInfosForScreens = creationInfos,
                hint = "TIA Portal GUI 에서 라이브러리의 master copy 를 드래그하면 됨. 자동화는 V20 에선 한계."
            });
        }

        static void HandleHmiUnifiedRoute(HttpListenerContext ctx, string route)
        {
            string hmiName = ctx.Request.QueryString["hmi"];
            if (string.IsNullOrEmpty(hmiName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?hmi= parameter (Unified HMI 디바이스 이름)" });
                return;
            }
            var hmi = FindHmiSoftware(hmiName);
            if (hmi == null)
            {
                SendJson(ctx.Response, 404, new { error = "Unified HMI not found: " + hmiName + " (HmiSoftware 가 있는 디바이스만 — Comfort/Advanced 는 /api/hmi/ 사용)" });
                return;
            }

            if (route == "connections/list") { ListUnifiedHmiConnections(ctx.Response, hmi); return; }
            if (route == "connection")
            {
                if (ctx.Request.HttpMethod == "POST") CreateUnifiedHmiConnection(ctx, hmi);
                else if (ctx.Request.HttpMethod == "DELETE") DeleteUnifiedHmiConnection(ctx, hmi);
                else if (ctx.Request.HttpMethod == "PUT") UpdateUnifiedHmiConnection(ctx, hmi);
                else GetUnifiedHmiConnectionDetail(ctx, hmi);
                return;
            }

            SendJson(ctx.Response, 404, new { error = "Unknown HMI Unified route: " + route });
        }

        static object BuildUnifiedHmiConnectionInfo(HmiConnection c)
        {
            return new
            {
                Name = SafeGet(() => c.Name),
                CommunicationDriver = SafeGet(() => c.CommunicationDriver),
                Partner = SafeGet(() => c.Partner),
                Station = SafeGet(() => c.Station),
                Node = SafeGet(() => c.Node),
                InitialAddress = SafeGet(() => c.InitialAddress),
                Comment = SafeGet(() => c.Comment != null ? c.Comment.ToString() : null),
                DisabledAtStartup = SafeGet(() => (object)c.DisabledAtStartup)
            };
        }

        static T SafeGet<T>(Func<T> f) where T : class
        {
            try { return f(); } catch { return null; }
        }

        static object SafeGet(Func<object> f)
        {
            try { return f(); } catch { return null; }
        }

        // GET /api/hmi-unified/connections/list?hmi=
        static void ListUnifiedHmiConnections(HttpListenerResponse resp, HmiSoftware hmi)
        {
            var list = new List<object>();
            try
            {
                foreach (HmiConnection c in hmi.Connections)
                {
                    list.Add(BuildUnifiedHmiConnectionInfo(c));
                }
            }
            catch (Exception ex)
            {
                SendJson(resp, 500, new { error = "Connections enumerate 실패: " + ex.Message });
                return;
            }
            SendJson(resp, 200, new { hmi = hmi.Name, count = list.Count, connections = list });
        }

        // GET /api/hmi-unified/connection?hmi=&name=
        static void GetUnifiedHmiConnectionDetail(HttpListenerContext ctx, HmiSoftware hmi)
        {
            string name = ctx.Request.QueryString["name"];
            if (string.IsNullOrEmpty(name))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?name= parameter" });
                return;
            }
            HmiConnection found = null;
            try { found = hmi.Connections.Find(name); } catch { }
            if (found == null)
            {
                foreach (HmiConnection c in hmi.Connections)
                    if (c.Name == name) { found = c; break; }
            }
            if (found == null)
            {
                SendJson(ctx.Response, 404, new { error = "Connection not found: " + name });
                return;
            }
            SendJson(ctx.Response, 200, BuildUnifiedHmiConnectionInfo(found));
        }

        // POST /api/hmi-unified/connection?hmi=&name=&driver=&partner=&station=&node=&initialAddress=
        // HmiConnectionComposition.Create(name) 으로 connection 생성, 그 후 Partner/Station/Node 설정.
        // Unified HMI 는 V20 Openness 가 typed Partner property 직접 노출 — Comfort 와 달리 자동화 가능!
        static void CreateUnifiedHmiConnection(HttpListenerContext ctx, HmiSoftware hmi)
        {
            string name = ctx.Request.QueryString["name"];
            string driver = ctx.Request.QueryString["driver"];
            string partner = ctx.Request.QueryString["partner"];
            string station = ctx.Request.QueryString["station"];
            string node = ctx.Request.QueryString["node"];
            string initialAddress = ctx.Request.QueryString["initialAddress"];

            if (string.IsNullOrEmpty(name))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?name= parameter" });
                return;
            }

            HmiConnection conn = null;
            try
            {
                conn = hmi.Connections.Create(name);
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                SendJson(ctx.Response, 500, new { error = "Create 실패: " + inner });
                return;
            }

            // 옵션 속성들 설정
            var setResults = new Dictionary<string, object>();
            ApplyConnectionAttribute(conn, "CommunicationDriver", driver, setResults);
            ApplyConnectionAttribute(conn, "Partner", partner, setResults);
            ApplyConnectionAttribute(conn, "Station", station, setResults);
            ApplyConnectionAttribute(conn, "Node", node, setResults);
            ApplyConnectionAttribute(conn, "InitialAddress", initialAddress, setResults);

            SendJson(ctx.Response, 200, new
            {
                message = "Unified HMI Connection created",
                connection = BuildUnifiedHmiConnectionInfo(conn),
                setResults = setResults
            });
        }

        // PUT /api/hmi-unified/connection?hmi=&name=&partner=&station=&node=&driver=&initialAddress=
        static void UpdateUnifiedHmiConnection(HttpListenerContext ctx, HmiSoftware hmi)
        {
            string name = ctx.Request.QueryString["name"];
            if (string.IsNullOrEmpty(name))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?name= parameter" });
                return;
            }
            HmiConnection conn = null;
            try { conn = hmi.Connections.Find(name); } catch { }
            if (conn == null)
            {
                foreach (HmiConnection c in hmi.Connections)
                    if (c.Name == name) { conn = c; break; }
            }
            if (conn == null)
            {
                SendJson(ctx.Response, 404, new { error = "Connection not found: " + name });
                return;
            }

            var q = ctx.Request.QueryString;
            var setResults = new Dictionary<string, object>();
            ApplyConnectionAttribute(conn, "CommunicationDriver", q["driver"], setResults);
            ApplyConnectionAttribute(conn, "Partner", q["partner"], setResults);
            ApplyConnectionAttribute(conn, "Station", q["station"], setResults);
            ApplyConnectionAttribute(conn, "Node", q["node"], setResults);
            ApplyConnectionAttribute(conn, "InitialAddress", q["initialAddress"], setResults);
            string disabled = q["disabledAtStartup"];
            if (disabled != null)
            {
                bool b;
                if (bool.TryParse(disabled, out b))
                {
                    try { conn.DisabledAtStartup = b; setResults["DisabledAtStartup"] = "OK -> " + b; }
                    catch (Exception ex) { setResults["DisabledAtStartup"] = "fail: " + ex.Message; }
                }
            }

            SendJson(ctx.Response, 200, new
            {
                message = "Unified HMI Connection updated",
                connection = BuildUnifiedHmiConnectionInfo(conn),
                setResults = setResults
            });
        }

        // SetAttribute 시도 (typed property 가 string 인 경우)
        static void ApplyConnectionAttribute(HmiConnection conn, string attr, string value, Dictionary<string, object> results)
        {
            if (value == null) return;
            try
            {
                var ieo = (IEngineeringObject)conn;
                ieo.SetAttribute(attr, value);
                results[attr] = "OK -> " + value;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                results[attr] = "fail: " + inner;
            }
        }

        // DELETE /api/hmi-unified/connection?hmi=&name=
        static void DeleteUnifiedHmiConnection(HttpListenerContext ctx, HmiSoftware hmi)
        {
            string name = ctx.Request.QueryString["name"];
            if (string.IsNullOrEmpty(name))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?name= parameter" });
                return;
            }
            HmiConnection conn = null;
            try { conn = hmi.Connections.Find(name); } catch { }
            if (conn == null)
            {
                foreach (HmiConnection c in hmi.Connections)
                    if (c.Name == name) { conn = c; break; }
            }
            if (conn == null)
            {
                SendJson(ctx.Response, 404, new { error = "Connection not found: " + name });
                return;
            }
            try
            {
                conn.Delete();
                SendJson(ctx.Response, 200, new { message = "Connection deleted", name });
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                SendJson(ctx.Response, 500, new { error = "Delete 실패: " + inner });
            }
        }


        // ── Comfort/Advanced HMI (HmiTarget) ──

        static void HandleHmiRoute(HttpListenerContext ctx, string route)
        {
            string hmiName = ctx.Request.QueryString["hmi"];
            if (string.IsNullOrEmpty(hmiName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?hmi= parameter" });
                return;
            }

            // Comfort/Advanced (HmiTarget) 와 Unified (HmiSoftware) 둘 다 시도
            var hmi = FindHmi(hmiName);
            HmiSoftware unified = (hmi == null) ? FindHmiSoftware(hmiName) : null;

            if (hmi == null && unified == null)
            {
                SendJson(ctx.Response, 404, new { error = "HMI not found: " + hmiName });
                return;
            }

            string method = ctx.Request.HttpMethod;

            // ── 통합 connection 라우트 (Comfort + Unified 둘 다 자동 분기) ──
            if (route == "connections/list")
            {
                if (hmi != null) ListHmiConnectionsDetailed(ctx.Response, hmi);
                else ListUnifiedHmiConnections(ctx.Response, unified);
                return;
            }
            if (route == "connection")
            {
                if (method == "POST")
                {
                    if (hmi != null) CreateHmiConnection(ctx, hmi);
                    else CreateUnifiedHmiConnection(ctx, unified);
                }
                else if (method == "DELETE")
                {
                    if (hmi != null) DeleteHmiConnection(ctx, hmi);
                    else DeleteUnifiedHmiConnection(ctx, unified);
                }
                else if (method == "PUT")
                {
                    if (unified != null) UpdateUnifiedHmiConnection(ctx, unified);
                    else SendJson(ctx.Response, 501, new { error = "PUT 은 Unified HMI 만 지원 (Comfort 는 Connection 속성 직접 수정 불가)" });
                }
                else  // GET
                {
                    if (hmi != null) ListHmiConnections(ctx.Response, hmi);
                    else
                    {
                        // Unified GET: ?name= 있으면 detail, 없으면 list
                        if (!string.IsNullOrEmpty(ctx.Request.QueryString["name"]))
                            GetUnifiedHmiConnectionDetail(ctx, unified);
                        else
                            ListUnifiedHmiConnections(ctx.Response, unified);
                    }
                }
                return;
            }
            if (route == "connection/detail")
            {
                if (hmi != null) GetHmiConnectionDetail(ctx, hmi);
                else GetUnifiedHmiConnectionDetail(ctx, unified);
                return;
            }

            // ── Unified 인 경우 — connection 관련 + screen MasterCopy 시도 endpoint, 그 외는 미지원 안내 ──
            if (unified != null)
            {
                // screens (Unified) — list 만 안전하게 (Export 는 V20 미지원이므로 enumerate 만)
                if (route == "screens")
                {
                    ListUnifiedHmiScreens(ctx.Response, unified);
                    return;
                }
                // screen → MasterCopy 생성 시도 (cross-project 이전 1단계)
                if (route == "screen/create-mastercopy")
                {
                    CreateMasterCopyFromUnifiedScreen(ctx, unified);
                    return;
                }
                // MasterCopy → screen 복원 시도 (cross-project 이전 2단계, V20 한계로 실패 가능)
                // POST 또는 GET 둘 다 허용 (테스트 편의)
                if (route == "screen/create-from-mastercopy")
                {
                    CreateUnifiedScreenFromMasterCopy(ctx, unified);
                    return;
                }
                if (route == "connection/export" || route == "connection/import" || route == "connections/export/zip")
                {
                    SendJson(ctx.Response, 501, new
                    {
                        error = "Unified HMI 는 Connection XML Export/Import 미지원 (V20 Openness 한계)",
                        hmi = hmiName,
                        kind = "HMI_Unified"
                    });
                    return;
                }
                SendJson(ctx.Response, 501, new
                {
                    error = "Unified HMI 에서 이 라우트는 지원되지 않습니다: " + route,
                    hmi = hmiName,
                    method = method,
                    receivedRoute = route,
                    kind = "HMI_Unified",
                    supportedForUnified = new[] {
                        "connections/list (GET)",
                        "connection (GET / POST / PUT / DELETE)",
                        "connection/detail (GET)",
                        "screens (GET, enumerate only)",
                        "screen/create-mastercopy (GET, MasterCopy 생성 시도)",
                        "screen/create-from-mastercopy (GET or POST, MasterCopy → screen 복원 시도)"
                    }
                });
                return;
            }

            // ── 아래는 Comfort/Advanced (HmiTarget) 전용 라우트 ──
            if (route == "tags")
            {
                bool detailed = ctx.Request.QueryString["detailed"] == "true";
                var tables = new List<object>();
                CollectHmiTags(hmi.TagFolder, "", tables, detailed);
                SendJson(ctx.Response, 200, new { count = tables.Count, tagTables = tables });
                return;
            }
            if (route == "tag-table") { GetHmiTagTable(ctx, hmi); return; }
            if (route == "tag-table/export") { ExportHmiTagTable(ctx, hmi); return; }
            if (route == "tag-table/import" && method == "POST") { ImportHmiTagTable(ctx, hmi); return; }
            if (route == "tags/export/zip") { ExportHmiTagsZip(ctx.Response, hmi); return; }
            if (route == "textlists")
            {
                var lists = new List<object>();
                foreach (TextList tl in hmi.TextLists)
                    lists.Add(new { name = tl.Name });
                SendJson(ctx.Response, 200, new { count = lists.Count, textLists = lists });
                return;
            }
            if (route == "screens") { GetHmiScreens(ctx.Response, hmi); return; }
            if (route == "screen-export") { ExportHmiScreen(ctx, hmi); return; }
            if (route == "screen-import" && method == "POST") { ImportHmiScreen(ctx, hmi); return; }
            if (route == "screens/export/zip") { ExportHmiScreensZip(ctx.Response, hmi); return; }
            if (route == "connection/export") { ExportHmiConnection(ctx, hmi); return; }
            if (route == "connection/import" && method == "POST") { ImportHmiConnection(ctx, hmi); return; }
            if (route == "connections/export/zip") { ExportHmiConnectionsZip(ctx.Response, hmi); return; }
            if (route == "network-connect" && method == "POST") { NetworkConnectHmiToCpu(ctx, hmi); return; }
            if (route == "_debug") { DumpHmiTarget(ctx.Response, hmi); return; }
            if (route == "connection/_inspect") { InspectHmiConnection(ctx, hmi); return; }
            if (route == "connections/_inspect-composition") { InspectConnectionComposition(ctx, hmi); return; }

            SendJson(ctx.Response, 404, new { error = "Unknown HMI route: " + route });
        }

        static DeviceItem FindCpuDeviceItem(string deviceItemName)
        {
            EnsureConnected();
            foreach (Project proj in connectedPortal.Projects)
            {
                foreach (Device device in proj.Devices)
                {
                    foreach (DeviceItem di in device.DeviceItems)
                    {
                        if (di.Name == deviceItemName)
                        {
                            var sc = ((IEngineeringServiceProvider)di).GetService<SoftwareContainer>();
                            if (sc != null && sc.Software is PlcSoftware) return di;
                        }
                        var found = FindCpuDeviceItemRec(di, deviceItemName);
                        if (found != null) return found;
                    }
                }
            }
            return null;
        }

        static DeviceItem FindCpuDeviceItemRec(DeviceItem di, string targetName)
        {
            if (di.Name == targetName)
            {
                var sc = ((IEngineeringServiceProvider)di).GetService<SoftwareContainer>();
                if (sc != null && sc.Software is PlcSoftware) return di;
            }
            foreach (DeviceItem sub in di.DeviceItems)
            {
                var found = FindCpuDeviceItemRec(sub, targetName);
                if (found != null) return found;
            }
            return null;
        }

        // HmiTarget.Connections 는 TIA Openness 버전에 따라 시그니처가 다르므로 reflection 사용.
        // 공통적으로 IEnumerable<Connection> 을 구현하며 Create(...) 오버로드를 제공한다.
        static void ListHmiConnections(HttpListenerResponse resp, HmiTarget hmi)
        {
            try
            {
                var connProp = hmi.GetType().GetProperty("Connections");
                if (connProp == null)
                {
                    SendJson(resp, 501, new { error = "HmiTarget.Connections property not available in this TIA Openness version" });
                    return;
                }
                var composition = connProp.GetValue(hmi);
                var list = new List<object>();
                if (composition is System.Collections.IEnumerable enumerable)
                {
                    foreach (var c in enumerable)
                    {
                        var nameProp = c.GetType().GetProperty("Name");
                        var typeProp = c.GetType().GetProperty("TypeIdentifier") ?? c.GetType().GetProperty("ConnectionType");
                        list.Add(new
                        {
                            name = nameProp != null ? nameProp.GetValue(c) as string : null,
                            typeIdentifier = typeProp != null ? typeProp.GetValue(c) as string : null
                        });
                    }
                }
                SendJson(resp, 200, new { count = list.Count, connections = list });
            }
            catch (Exception ex)
            {
                SendJson(resp, 500, new { error = ex.Message, type = ex.GetType().FullName });
            }
        }

        static void CreateHmiConnection(HttpListenerContext ctx, HmiTarget hmi)
        {
            string cpuName = ctx.Request.QueryString["cpu"];
            string connName = ctx.Request.QueryString["name"];
            string typeId = ctx.Request.QueryString["typeId"];
            if (string.IsNullOrEmpty(typeId)) typeId = "HmiS7Connection";

            if (string.IsNullOrEmpty(cpuName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?cpu= parameter (CPU DeviceItem name)" });
                return;
            }
            if (string.IsNullOrEmpty(connName)) connName = "HMI_Connection_1";

            var cpuItem = FindCpuDeviceItem(cpuName);
            if (cpuItem == null)
            {
                SendJson(ctx.Response, 404, new { error = "CPU DeviceItem not found: " + cpuName });
                return;
            }

            try
            {
                var connProp = hmi.GetType().GetProperty("Connections");
                if (connProp == null)
                {
                    SendJson(ctx.Response, 501, new { error = "HmiTarget.Connections property not available in this TIA Openness version" });
                    return;
                }
                var composition = connProp.GetValue(hmi);
                if (composition == null)
                {
                    SendJson(ctx.Response, 500, new { error = "HmiTarget.Connections is null" });
                    return;
                }

                var createMethods = composition.GetType().GetMethods()
                    .Where(m => m.Name == "Create")
                    .ToList();

                var errors = new List<string>();
                object createdConn = null;

                foreach (var m in createMethods)
                {
                    var ps = m.GetParameters();
                    try
                    {
                        object[] args = null;
                        // 흔한 오버로드 패턴:
                        //  1) Create(string name, string typeIdentifier)
                        //  2) Create(string name, string typeIdentifier, DeviceItem partner)
                        //  3) Create(string name, DeviceItem partner)
                        if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
                            args = new object[] { connName, typeId };
                        else if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && typeof(DeviceItem).IsAssignableFrom(ps[1].ParameterType))
                            args = new object[] { connName, cpuItem };
                        else if (ps.Length == 3 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string) && typeof(DeviceItem).IsAssignableFrom(ps[2].ParameterType))
                            args = new object[] { connName, typeId, cpuItem };
                        else
                        {
                            errors.Add("Skipped overload: " + string.Join(", ", ps.Select(p => p.ParameterType.Name)));
                            continue;
                        }
                        createdConn = m.Invoke(composition, args);
                        if (createdConn != null) break;
                    }
                    catch (Exception invEx)
                    {
                        var inner = invEx.InnerException != null ? invEx.InnerException.Message : invEx.Message;
                        errors.Add("Overload(" + string.Join(",", ps.Select(p => p.ParameterType.Name)) + "): " + inner);
                    }
                }

                if (createdConn == null)
                {
                    SendJson(ctx.Response, 500, new
                    {
                        error = "Failed to create HMI connection via any known Create overload",
                        attempts = errors,
                        availableMethods = createMethods.Select(m => m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")").ToList()
                    });
                    return;
                }

                // Partner 속성이 있으면 CPU DeviceItem 로 설정 시도
                var partnerProp = createdConn.GetType().GetProperty("Partner");
                if (partnerProp != null && partnerProp.CanWrite)
                {
                    try { partnerProp.SetValue(createdConn, cpuItem); } catch { /* best-effort */ }
                }

                var nameProp = createdConn.GetType().GetProperty("Name");
                SendJson(ctx.Response, 201, new
                {
                    message = "HMI connection created",
                    hmi = hmi.Name,
                    cpu = cpuName,
                    name = nameProp != null ? nameProp.GetValue(createdConn) as string : connName,
                    typeIdentifier = typeId
                });
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException != null ? ex.InnerException.Message : null;
                SendJson(ctx.Response, 500, new { error = ex.Message, inner = inner, type = ex.GetType().FullName });
            }
        }

        // ── HMI Connection (Siemens.Engineering.Hmi.Communication) ──

        // hmi.Connections 가 ConnectionComposition 으로 typed 인지 확인 후 반환.
        // 일부 HMI(예: Unified) 는 이 property 자체가 없을 수 있어 null 가능.
        static ConnectionComposition GetHmiConnections(HmiTarget hmi)
        {
            try
            {
                var prop = hmi.GetType().GetProperty("Connections");
                if (prop == null) return null;
                return prop.GetValue(hmi) as ConnectionComposition;
            }
            catch { return null; }
        }

        static Connection FindHmiConnection(HmiTarget hmi, string name)
        {
            var comp = GetHmiConnections(hmi);
            if (comp == null) return null;
            try { return comp.Find(name); } catch { }
            // Find 가 실패하거나 없으면 순회 fallback
            try
            {
                foreach (Connection c in comp)
                    if (c.Name == name) return c;
            }
            catch { }
            return null;
        }

        static Dictionary<string, object> ExtractHmiConnectionAttributes(Connection conn)
        {
            var attrs = new Dictionary<string, object>();
            try { attrs["Name"] = conn.Name; } catch { }

            IEngineeringObject ieo = null;
            try { ieo = (IEngineeringObject)conn; } catch { }
            if (ieo != null)
            {
                System.Collections.IEnumerable infos = null;
                try { infos = ieo.GetAttributeInfos(); } catch { }
                if (infos != null)
                {
                    foreach (var info in infos)
                    {
                        string n = null;
                        try { n = info.GetType().GetProperty("Name").GetValue(info) as string; }
                        catch { }
                        if (string.IsNullOrEmpty(n)) continue;
                        if (attrs.ContainsKey(n)) continue;
                        try
                        {
                            var v = ieo.GetAttribute(n);
                            attrs[n] = NormalizeAttributeValue(v);
                        }
                        catch { attrs[n] = null; }
                    }
                }
            }
            return attrs;
        }

        // GET /api/hmi/connections/list — 모든 connection 의 attribute 상세 dump
        static void ListHmiConnectionsDetailed(HttpListenerResponse resp, HmiTarget hmi)
        {
            var comp = GetHmiConnections(hmi);
            if (comp == null)
            {
                SendJson(resp, 200, new { hmi = hmi.Name, count = 0, connections = new List<object>(), note = "HmiTarget.Connections 사용 불가 (Unified HMI 등)" });
                return;
            }
            var list = new List<object>();
            foreach (Connection conn in comp)
            {
                list.Add(ExtractHmiConnectionAttributes(conn));
            }
            SendJson(resp, 200, new { hmi = hmi.Name, count = list.Count, connections = list });
        }

        // GET /api/hmi/connection/detail?hmi=&name=
        static void GetHmiConnectionDetail(HttpListenerContext ctx, HmiTarget hmi)
        {
            string name = ctx.Request.QueryString["name"];
            if (string.IsNullOrEmpty(name))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?name= parameter" });
                return;
            }
            var conn = FindHmiConnection(hmi, name);
            if (conn == null)
            {
                SendJson(ctx.Response, 404, new { error = "Connection not found: " + name });
                return;
            }
            var attrs = ExtractHmiConnectionAttributes(conn);
            SendJson(ctx.Response, 200, new { hmi = hmi.Name, name = name, attributes = attrs });
        }

        // GET /api/hmi/connection/export?hmi=&name=
        static void ExportHmiConnection(HttpListenerContext ctx, HmiTarget hmi)
        {
            string name = ctx.Request.QueryString["name"];
            if (string.IsNullOrEmpty(name))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?name= parameter" });
                return;
            }
            var conn = FindHmiConnection(hmi, name);
            if (conn == null)
            {
                SendJson(ctx.Response, 404, new { error = "Connection not found: " + name });
                return;
            }
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            string fileName = MakeValidFileName(name) + ".xml";
            string tempFile = Path.Combine(tempDir, fileName);
            if (File.Exists(tempFile)) File.Delete(tempFile);
            try
            {
                conn.Export(new FileInfo(tempFile), ExportOptions.WithDefaults);
                Console.WriteLine("HMI connection exported: " + name);
                SendFile(ctx.Response, tempFile, fileName);
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Connection export failed: " + ex.Message, type = ex.GetType().Name });
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        // POST /api/hmi/connection/import?hmi=
        static void ImportHmiConnection(HttpListenerContext ctx, HmiTarget hmi)
        {
            var comp = GetHmiConnections(hmi);
            if (comp == null)
            {
                SendJson(ctx.Response, 500, new { error = "HmiTarget.Connections 사용 불가" });
                return;
            }

            byte[] body;
            using (var ms = new MemoryStream())
            {
                ctx.Request.InputStream.CopyTo(ms);
                body = ms.ToArray();
            }
            if (body.Length == 0)
            {
                SendJson(ctx.Response, 400, new { error = "Empty body. POST the connection XML content." });
                return;
            }

            string tempFile = Path.Combine(tempDir, "hmi_conn_import_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".xml");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(tempFile, body);
            string shortPath = CopyToFlat(tempFile, @"C:\T\hmiconnimport");
            File.Delete(tempFile);

            try
            {
                comp.Import(new FileInfo(shortPath), ImportOptions.Override);
                Console.WriteLine("HMI connection imported");
                SendJson(ctx.Response, 200, new { message = "HMI connection imported", hmi = hmi.Name });
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Connection import failed: " + ex.Message, type = ex.GetType().Name });
            }
            finally
            {
                if (File.Exists(shortPath)) File.Delete(shortPath);
            }
        }

        // GET /api/hmi/connections/export/zip?hmi=
        static void ExportHmiConnectionsZip(HttpListenerResponse resp, HmiTarget hmi)
        {
            var comp = GetHmiConnections(hmi);
            if (comp == null)
            {
                SendJson(resp, 500, new { error = "HmiTarget.Connections 사용 불가" });
                return;
            }
            string exportDir = Path.Combine(tempDir, "hmi_conn_" + Guid.NewGuid().ToString().Substring(0, 8));
            Directory.CreateDirectory(exportDir);
            string zipFile = Path.Combine(tempDir, "hmi_connections_" + MakeValidFileName(hmi.Name) + ".zip");
            if (File.Exists(zipFile)) File.Delete(zipFile);

            int success = 0, failed = 0;
            try
            {
                using (var zip = ZipFile.Open(zipFile, ZipArchiveMode.Create))
                {
                    foreach (Connection conn in comp)
                    {
                        string fileName = MakeValidFileName(conn.Name) + ".xml";
                        string tempFile = Path.Combine(exportDir, fileName);
                        try
                        {
                            if (File.Exists(tempFile)) File.Delete(tempFile);
                            conn.Export(new FileInfo(tempFile), ExportOptions.WithDefaults);
                            if (File.Exists(tempFile))
                            {
                                zip.CreateEntryFromFile(tempFile, "Connections/" + fileName);
                                File.Delete(tempFile);
                                Console.WriteLine("  HMI Conn: " + fileName);
                                success++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("  HMI Conn export error [" + conn.Name + "]: " + ex.Message);
                            failed++;
                        }
                    }
                }
                Console.WriteLine("HMI connections exported: " + success + " ok, " + failed + " failed");
                SendFileWithExportReport(resp, zipFile, "hmi_connections_" + MakeValidFileName(hmi.Name) + ".zip", success, failed);
            }
            catch (Exception ex)
            {
                SendJson(resp, 500, new { error = "HMI connections ZIP export failed: " + ex.Message });
            }
            finally
            {
                if (File.Exists(zipFile)) File.Delete(zipFile);
                if (Directory.Exists(exportDir)) Directory.Delete(exportDir, true);
            }
        }

        // DELETE /api/hmi/connection?hmi=&name=
        static void DeleteHmiConnection(HttpListenerContext ctx, HmiTarget hmi)
        {
            string name = ctx.Request.QueryString["name"];
            if (string.IsNullOrEmpty(name))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?name= parameter" });
                return;
            }
            var conn = FindHmiConnection(hmi, name);
            if (conn == null)
            {
                SendJson(ctx.Response, 404, new { error = "Connection not found: " + name });
                return;
            }
            try
            {
                conn.Delete();
                SendJson(ctx.Response, 200, new { message = "Connection deleted", hmi = hmi.Name, name = name });
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Connection delete failed: " + ex.Message, type = ex.GetType().Name });
            }
        }

        // GET /api/hmi/_debug?hmi= — HmiTarget 의 모든 property/composition/service 를 dump.
        // Integrated connection 같은 hidden 객체가 어느 composition 에 있는지 찾는 용도.
        static void DumpHmiTarget(HttpListenerResponse resp, HmiTarget hmi)
        {
            var result = new Dictionary<string, object>();
            result["__type"] = hmi.GetType().FullName;
            try { result["Name"] = hmi.Name; } catch { }

            // 1) public 프로퍼티 dump (collection 은 count + 첫 5개 이름만)
            var props = new Dictionary<string, object>();
            foreach (var p in hmi.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (p.Name == "Parent") continue;
                try
                {
                    var v = p.GetValue(hmi);
                    if (v == null) { props[p.Name] = null; continue; }
                    var t = v.GetType();
                    if (t == typeof(string) || t.IsPrimitive || t.IsEnum || t == typeof(DateTime) || t == typeof(Guid))
                    {
                        props[p.Name] = v;
                        continue;
                    }
                    if (v is System.Collections.IEnumerable enu)
                    {
                        int count = 0;
                        var firstNames = new List<string>();
                        foreach (var item in enu)
                        {
                            count++;
                            if (firstNames.Count < 10)
                            {
                                string n = null;
                                try { n = item.GetType().GetProperty("Name")?.GetValue(item) as string; } catch { }
                                firstNames.Add(n ?? item.GetType().Name);
                            }
                        }
                        props[p.Name] = new { __type = t.FullName, count = count, items = firstNames };
                        continue;
                    }
                    props[p.Name] = new { __type = t.FullName, value = v.ToString() };
                }
                catch (Exception ex) { props[p.Name] = new { error = ex.Message }; }
            }
            result["properties"] = props;

            // 2) IEngineeringObject.GetCompositionInfos() — hidden composition 까지 노출
            var compositions = new List<object>();
            try
            {
                var ieo = (IEngineeringObject)hmi;
                var compInfos = ieo.GetCompositionInfos();
                foreach (var info in compInfos)
                {
                    string name = null;
                    try { name = info.GetType().GetProperty("Name")?.GetValue(info) as string; } catch { }
                    if (string.IsNullOrEmpty(name)) continue;

                    // 해당 composition 호출
                    object compObj = null;
                    int count = 0;
                    var firstNames = new List<string>();
                    string compTypeName = null;
                    try
                    {
                        compObj = ieo.GetComposition(name);
                        if (compObj != null)
                        {
                            compTypeName = compObj.GetType().FullName;
                            if (compObj is System.Collections.IEnumerable enu)
                            {
                                foreach (var item in enu)
                                {
                                    count++;
                                    if (firstNames.Count < 10)
                                    {
                                        string itemName = null;
                                        try { itemName = item.GetType().GetProperty("Name")?.GetValue(item) as string; } catch { }
                                        firstNames.Add(itemName ?? item.GetType().Name);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        compositions.Add(new { name = name, error = ex.Message });
                        continue;
                    }
                    compositions.Add(new { name = name, type = compTypeName, count = count, items = firstNames });
                }
            }
            catch (Exception ex) { result["compositionDumpError"] = ex.Message; }
            result["compositions"] = compositions;

            // 3) GetServiceInfos() — 사용 가능한 서비스 (드물게 connection 관련 service 가 있음)
            var services = new List<string>();
            try
            {
                var sip = hmi as IEngineeringServiceProvider;
                if (sip != null)
                {
                    var infos = sip.GetServiceInfos();
                    foreach (var info in infos)
                    {
                        string svcType = null;
                        try { svcType = info.GetType().GetProperty("Type")?.GetValue(info)?.ToString(); } catch { }
                        if (svcType == null)
                        {
                            try { svcType = info.GetType().GetProperty("ServiceType")?.GetValue(info)?.ToString(); } catch { }
                        }
                        services.Add(svcType ?? info.GetType().FullName);
                    }
                }
            }
            catch (Exception ex) { result["serviceDumpError"] = ex.Message; }
            result["services"] = services;

            SendJson(resp, 200, result);
        }

        // GET /api/hmi/connection/_inspect?hmi=&name=
        // 단일 Connection 객체의 모든 가능한 metadata 를 dump:
        //  - GetAttributeInfos / GetAttributes(All access options)
        //  - GetCompositionInfos
        //  - GetInvocationInfos (hidden methods)
        //  - GetType().GetProperties / GetMethods (reflection)
        // Partner 같은 hidden attribute / method 가 있는지 찾기 위한 깊은 진단.
        static void InspectHmiConnection(HttpListenerContext ctx, HmiTarget hmi)
        {
            string name = ctx.Request.QueryString["name"];
            if (string.IsNullOrEmpty(name))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?name= parameter" });
                return;
            }
            var conn = FindHmiConnection(hmi, name);
            if (conn == null)
            {
                SendJson(ctx.Response, 404, new { error = "Connection not found: " + name });
                return;
            }

            var result = new Dictionary<string, object>();
            result["__type"] = conn.GetType().FullName;
            result["Name"] = conn.Name;

            var ieo = (IEngineeringObject)conn;

            // 1) GetAttributeInfos
            var attrInfos = new List<object>();
            try
            {
                foreach (var info in ieo.GetAttributeInfos())
                {
                    string n = null; string typeStr = null;
                    try { n = info.GetType().GetProperty("Name")?.GetValue(info) as string; } catch { }
                    foreach (var pname in new[] { "AccessMode", "Access", "AccessOptions" })
                    {
                        try
                        {
                            var v = info.GetType().GetProperty(pname)?.GetValue(info);
                            if (v != null) { typeStr = pname + "=" + v.ToString(); break; }
                        }
                        catch { }
                    }
                    attrInfos.Add(new { name = n, access = typeStr });
                }
            }
            catch (Exception ex) { attrInfos.Add(new { error = ex.Message }); }
            result["attributeInfos"] = attrInfos;

            // 2) GetAttributes 4가지 access option 모두 시도
            var accessOptions = new[]
            {
                AttributeAccessOptions.None,
                AttributeAccessOptions.ReadOnly,
                AttributeAccessOptions.WriteOnly,
                AttributeAccessOptions.ReadWrite
            };
            var attrsByMode = new Dictionary<string, object>();
            foreach (var opt in accessOptions)
            {
                try
                {
                    var attrs = ieo.GetAttributes(opt);
                    var dict = new Dictionary<string, object>();
                    foreach (var kv in attrs)
                    {
                        dict[kv.Key] = NormalizeAttributeValue(kv.Value);
                    }
                    attrsByMode[opt.ToString()] = dict;
                }
                catch (Exception ex) { attrsByMode[opt.ToString()] = new { error = ex.Message }; }
            }
            result["attributesByAccessMode"] = attrsByMode;

            // 3) GetCompositionInfos — sub-collections
            var compositionInfos = new List<object>();
            try
            {
                var infos = ieo.GetCompositionInfos();
                foreach (var info in infos)
                {
                    string n = null;
                    try { n = info.GetType().GetProperty("Name")?.GetValue(info) as string; } catch { }
                    if (string.IsNullOrEmpty(n)) continue;

                    int count = 0; var firstNames = new List<string>(); string compType = null;
                    try
                    {
                        var compObj = ieo.GetComposition(n);
                        if (compObj != null)
                        {
                            compType = compObj.GetType().FullName;
                            if (compObj is System.Collections.IEnumerable enu)
                            {
                                foreach (var item in enu)
                                {
                                    count++;
                                    if (firstNames.Count < 10)
                                    {
                                        string itemName = null;
                                        try { itemName = item.GetType().GetProperty("Name")?.GetValue(item) as string; } catch { }
                                        firstNames.Add(itemName ?? item.GetType().Name);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex) { compositionInfos.Add(new { name = n, error = ex.Message }); continue; }
                    compositionInfos.Add(new { name = n, type = compType, count = count, items = firstNames });
                }
            }
            catch (Exception ex) { compositionInfos.Add(new { error = ex.Message }); }
            result["compositions"] = compositionInfos;

            // 4) GetInvocationInfos — hidden methods (Invoke 가능한 명령들)
            var invocationInfos = new List<object>();
            try
            {
                var infos = ieo.GetInvocationInfos();
                foreach (var info in infos)
                {
                    string n = null;
                    try { n = info.GetType().GetProperty("Name")?.GetValue(info) as string; } catch { }
                    var paramList = new List<object>();
                    try
                    {
                        var paramsProp = info.GetType().GetProperty("Parameters");
                        var ps = paramsProp?.GetValue(info) as System.Collections.IEnumerable;
                        if (ps != null)
                        {
                            foreach (var p in ps)
                            {
                                string pn = null, pt = null;
                                try { pn = p.GetType().GetProperty("Name")?.GetValue(p) as string; } catch { }
                                try { pt = p.GetType().GetProperty("ParameterType")?.GetValue(p)?.ToString(); } catch { }
                                if (pt == null) try { pt = p.GetType().GetProperty("Type")?.GetValue(p)?.ToString(); } catch { }
                                paramList.Add(new { name = pn, type = pt });
                            }
                        }
                    }
                    catch { }
                    invocationInfos.Add(new { name = n, parameters = paramList });
                }
            }
            catch (Exception ex) { invocationInfos.Add(new { error = ex.Message }); }
            result["invocationInfos"] = invocationInfos;

            // 5) Reflection: 직접 properties / methods (typed)
            var typedProperties = new List<string>();
            foreach (var p in conn.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                typedProperties.Add(p.Name + ":" + p.PropertyType.Name);
            result["typedProperties"] = typedProperties;

            var typedMethods = new List<string>();
            foreach (var m in conn.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.IsSpecialName) continue;
                typedMethods.Add(m.Name + "(" + string.Join(",", m.GetParameters().Select(pp => pp.ParameterType.Name)) + ")");
            }
            result["typedMethods"] = typedMethods;

            SendJson(ctx.Response, 200, result);
        }

        // GET /api/hmi/connections/_inspect-composition?hmi=
        // ConnectionComposition 자체의 hidden API 탐색:
        //  - GetCreationInfos(name) — Create 가 받는 Type 후보들 (integrated connection 별도 Type 이 있을 수도)
        //  - GetInvocationInfos — composition 레벨 hidden methods
        //  - 각 access mode 의 attribute
        static void InspectConnectionComposition(HttpListenerContext ctx, HmiTarget hmi)
        {
            var comp = GetHmiConnections(hmi);
            if (comp == null)
            {
                SendJson(ctx.Response, 404, new { error = "ConnectionComposition not available" });
                return;
            }

            var result = new Dictionary<string, object>();
            result["__type"] = comp.GetType().FullName;

            // Count vs foreach 비교 — 다르면 hidden item 존재
            int countProperty = -1, foreachCount = 0;
            try { countProperty = comp.Count; } catch { }
            try { foreach (Connection c in comp) foreachCount++; } catch { }
            result["countProperty"] = countProperty;
            result["foreachCount"] = foreachCount;
            result["hiddenItemsLikely"] = countProperty != foreachCount;

            // Item indexer 로 모든 item 읽기 시도 (foreach 가 막혀도 indexer 는 통할 수 있음)
            var itemsViaIndexer = new List<object>();
            for (int i = 0; i < countProperty && i < 50; i++)
            {
                try
                {
                    var item = comp[i];
                    string n = null;
                    try { n = item?.Name; } catch { }
                    itemsViaIndexer.Add(new { index = i, name = n, type = item?.GetType().FullName });
                }
                catch (Exception ex) { itemsViaIndexer.Add(new { index = i, error = ex.Message }); }
            }
            result["itemsViaIndexer"] = itemsViaIndexer;

            // ConnectionComposition 은 IEngineeringObject 를 직접 구현하지 않으므로 reflection 으로 호출
            var compType = comp.GetType();
            var getCompositionInfos = compType.GetMethod("GetCompositionInfos", new Type[0]);
            var getInvocationInfos = compType.GetMethod("GetInvocationInfos", new Type[0]);
            var getAttributeInfos = compType.GetMethod("GetAttributeInfos", new Type[0]);
            var getCreationInfos = compType.GetMethod("GetCreationInfos", new[] { typeof(string) });

            if (getCompositionInfos != null || getInvocationInfos != null)
            {
                // GetCompositionInfos
                var compInfos = new List<object>();
                try
                {
                    var infos = getCompositionInfos?.Invoke(comp, null) as System.Collections.IEnumerable;
                    if (infos != null)
                    {
                        foreach (var info in infos)
                        {
                            string n = null;
                            try { n = info.GetType().GetProperty("Name")?.GetValue(info) as string; } catch { }
                            compInfos.Add(new { name = n });
                        }
                    }
                }
                catch (Exception ex) { compInfos.Add(new { error = ex.Message }); }
                result["compositionInfos"] = compInfos;

                // GetInvocationInfos
                var invInfos = new List<object>();
                try
                {
                    var infos = getInvocationInfos?.Invoke(comp, null) as System.Collections.IEnumerable;
                    if (infos != null)
                    {
                        foreach (var info in infos)
                        {
                            string n = null;
                            try { n = info.GetType().GetProperty("Name")?.GetValue(info) as string; } catch { }
                            var paramList = new List<object>();
                            try
                            {
                                var ps = info.GetType().GetProperty("Parameters")?.GetValue(info) as System.Collections.IEnumerable;
                                if (ps != null)
                                {
                                    foreach (var p in ps)
                                    {
                                        string pn = null, pt = null;
                                        try { pn = p.GetType().GetProperty("Name")?.GetValue(p) as string; } catch { }
                                        try { pt = p.GetType().GetProperty("ParameterType")?.GetValue(p)?.ToString(); } catch { }
                                        if (pt == null) try { pt = p.GetType().GetProperty("Type")?.GetValue(p)?.ToString(); } catch { }
                                        paramList.Add(new { name = pn, type = pt });
                                    }
                                }
                            }
                            catch { }
                            invInfos.Add(new { name = n, parameters = paramList });
                        }
                    }
                }
                catch (Exception ex) { invInfos.Add(new { error = ex.Message }); }
                result["invocationInfos"] = invInfos;

                // GetCreationInfos for various candidate names — Create 가 받을 수 있는 타입 후보 확인
                var creationInfos = new Dictionary<string, object>();
                var candidateNames = new[] { "Connection", "Connections", "HmiConnection", "IntegratedConnection", "S7Connection", "" };
                foreach (var cn in candidateNames)
                {
                    try
                    {
                        var info = getCreationInfos?.Invoke(comp, new object[] { cn });
                        if (info != null)
                        {
                            var typesList = new List<string>();
                            try
                            {
                                if (info is System.Collections.IEnumerable infoEnu)
                                {
                                    foreach (var ci in infoEnu)
                                    {
                                        string tn = null;
                                        try { tn = ci.GetType().GetProperty("Type")?.GetValue(ci)?.ToString(); } catch { }
                                        if (tn == null) try { tn = ci.ToString(); } catch { }
                                        typesList.Add(tn ?? "<null>");
                                    }
                                }
                                else
                                {
                                    typesList.Add(info.ToString());
                                }
                            }
                            catch { }
                            creationInfos[string.IsNullOrEmpty(cn) ? "(empty)" : cn] = typesList;
                        }
                    }
                    catch (Exception ex)
                    {
                        var inner = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                        creationInfos[string.IsNullOrEmpty(cn) ? "(empty)" : cn] = "fail: " + inner;
                    }
                }
                result["creationInfos"] = creationInfos;

                // GetAttributeInfos
                var attrInfos = new List<string>();
                try
                {
                    var infos = getAttributeInfos?.Invoke(comp, null) as System.Collections.IEnumerable;
                    if (infos != null)
                    {
                        foreach (var info in infos)
                        {
                            string n = null;
                            try { n = info.GetType().GetProperty("Name")?.GetValue(info) as string; } catch { }
                            if (n != null) attrInfos.Add(n);
                        }
                    }
                }
                catch { }
                result["attributeInfos"] = attrInfos;
            }

            // 직접 reflection: composition 의 모든 public method (Create 류 발견 목적)
            var methods = new List<string>();
            foreach (var m in comp.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.IsSpecialName) continue;
                methods.Add(m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
            }
            result["typedMethods"] = methods;

            // composition 의 Properties
            var properties = new List<string>();
            foreach (var p in comp.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                properties.Add(p.Name + ":" + p.PropertyType.Name);
            result["typedProperties"] = properties;

            SendJson(ctx.Response, 200, result);
        }

        // POST /api/hmi/network-connect?hmi=&cpu=&subnet=&hmiIp=&cpuIp=&createConnection=&connectionName=&typeId=
        // TIA Portal 의 "Devices & Networks 에서 HMI → CPU 드래그" 와 동일한 동작:
        //  1) HMI/CPU NetworkInterface 의 Node 를 같은 Subnet 에 연결
        //  2) (옵션) HMI Connection 을 partner=CPU DeviceItem 으로 생성
        // 기존 partial 한 CreateHmiConnection 보다 한 번에 통합 셋업.
        static void NetworkConnectHmiToCpu(HttpListenerContext ctx, HmiTarget hmi)
        {
            string cpuName = ctx.Request.QueryString["cpu"];
            string subnetName = ctx.Request.QueryString["subnet"];
            string hmiIp = ctx.Request.QueryString["hmiIp"];
            string cpuIp = ctx.Request.QueryString["cpuIp"];
            string connectionName = ctx.Request.QueryString["connectionName"];
            string typeId = ctx.Request.QueryString["typeId"] ?? "HmiS7Connection";
            bool createConnection = (ctx.Request.QueryString["createConnection"] ?? "true").ToLower() != "false";

            if (string.IsNullOrEmpty(cpuName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?cpu= parameter (CPU DeviceItem name)" });
                return;
            }

            // CPU DeviceItem (PlcSoftware 가진 것)
            var cpuItem = FindCpuDeviceItem(cpuName);
            if (cpuItem == null)
            {
                SendJson(ctx.Response, 404, new { error = "CPU DeviceItem not found: " + cpuName });
                return;
            }
            var cpuDevice = FindParentDevice(cpuItem);
            if (cpuDevice == null)
            {
                SendJson(ctx.Response, 404, new { error = "CPU의 부모 Device 를 찾을 수 없습니다: " + cpuName });
                return;
            }

            // HMI Device 찾기 (HmiTarget → DeviceItem 부모 트리 탐색)
            Device hmiDevice = null;
            try
            {
                var ieo = (IEngineeringObject)hmi;
                var parent = ieo.Parent;
                while (parent != null && hmiDevice == null)
                {
                    hmiDevice = parent as Device;
                    if (hmiDevice != null) break;
                    parent = parent.Parent;
                }
            }
            catch { }
            if (hmiDevice == null)
            {
                SendJson(ctx.Response, 404, new { error = "HMI 의 부모 Device 를 찾을 수 없습니다" });
                return;
            }

            // 양쪽 Network Interface — type 매칭 기반 선택
            // CPU 가 PROFIBUS + Ethernet 둘 다 가질 수 있어 [0] 무조건 선택은 위험.
            // ?hmiInterfaceIndex= / ?cpuInterfaceIndex= 로 강제 지정도 가능.
            var hmiInterfaces = FindAllNetworkInterfaces(hmiDevice);
            var cpuInterfaces = FindAllNetworkInterfaces(cpuDevice);
            if (hmiInterfaces.Count == 0)
            {
                SendJson(ctx.Response, 404, new { error = "HMI 에 NetworkInterface 가 없습니다" });
                return;
            }
            if (cpuInterfaces.Count == 0)
            {
                SendJson(ctx.Response, 404, new { error = "CPU 에 NetworkInterface 가 없습니다" });
                return;
            }

            int hmiIfIdx = -1, cpuIfIdx = -1;
            int.TryParse(ctx.Request.QueryString["hmiInterfaceIndex"] ?? "-1", out hmiIfIdx);
            int.TryParse(ctx.Request.QueryString["cpuInterfaceIndex"] ?? "-1", out cpuIfIdx);

            NetworkInterface hmiNetIf = null, cpuNetIf = null;
            if (hmiIfIdx >= 0 && hmiIfIdx < hmiInterfaces.Count) hmiNetIf = hmiInterfaces[hmiIfIdx].Item1;
            else
            {
                // 기본: Ethernet 우선, 없으면 첫 번째
                foreach (var ifInfo in hmiInterfaces)
                {
                    if (ifInfo.Item1.InterfaceType.ToString() == "Ethernet") { hmiNetIf = ifInfo.Item1; break; }
                }
                if (hmiNetIf == null) hmiNetIf = hmiInterfaces[0].Item1;
            }

            if (cpuIfIdx >= 0 && cpuIfIdx < cpuInterfaces.Count) cpuNetIf = cpuInterfaces[cpuIfIdx].Item1;
            else
            {
                // 기본: HMI 와 같은 InterfaceType 찾기, 없으면 Ethernet, 없으면 첫 번째
                string hmiType = hmiNetIf.InterfaceType.ToString();
                foreach (var ifInfo in cpuInterfaces)
                {
                    if (ifInfo.Item1.InterfaceType.ToString() == hmiType) { cpuNetIf = ifInfo.Item1; break; }
                }
                if (cpuNetIf == null)
                {
                    foreach (var ifInfo in cpuInterfaces)
                    {
                        if (ifInfo.Item1.InterfaceType.ToString() == "Ethernet") { cpuNetIf = ifInfo.Item1; break; }
                    }
                }
                if (cpuNetIf == null) cpuNetIf = cpuInterfaces[0].Item1;
            }

            // 인터페이스 타입 불일치 — 명시적으로 알림
            if (hmiNetIf.InterfaceType.ToString() != cpuNetIf.InterfaceType.ToString())
            {
                SendJson(ctx.Response, 409, new
                {
                    error = "선택된 NetworkInterface 의 type 이 일치하지 않습니다",
                    hmiInterfaceType = hmiNetIf.InterfaceType.ToString(),
                    cpuInterfaceType = cpuNetIf.InterfaceType.ToString(),
                    hmiInterfaces = hmiInterfaces.Select((it, i) => new { index = i, type = it.Item1.InterfaceType.ToString(), itemName = it.Item2.Name }).ToList(),
                    cpuInterfaces = cpuInterfaces.Select((it, i) => new { index = i, type = it.Item1.InterfaceType.ToString(), itemName = it.Item2.Name }).ToList(),
                    hint = "?hmiInterfaceIndex= 또는 ?cpuInterfaceIndex= 로 매칭되는 interface 인덱스 명시"
                });
                return;
            }

            // Node 가져오기 (없으면 첫번째 생성하지 않고 에러 — Node 는 Interface 에 자동으로 있음)
            Node hmiNode = null, cpuNode = null;
            try { foreach (Node n in hmiNetIf.Nodes) { hmiNode = n; break; } } catch { }
            try { foreach (Node n in cpuNetIf.Nodes) { cpuNode = n; break; } } catch { }
            if (hmiNode == null) { SendJson(ctx.Response, 404, new { error = "HMI Node 가 없습니다" }); return; }
            if (cpuNode == null) { SendJson(ctx.Response, 404, new { error = "CPU Node 가 없습니다" }); return; }

            // IP 셋업 (옵션)
            var setIpResults = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(hmiIp))
            {
                try { hmiNode.SetAttribute("Address", hmiIp); setIpResults["hmiIp"] = hmiIp; }
                catch (Exception ex) { setIpResults["hmiIpError"] = ex.Message; }
            }
            if (!string.IsNullOrEmpty(cpuIp))
            {
                try { cpuNode.SetAttribute("Address", cpuIp); setIpResults["cpuIp"] = cpuIp; }
                catch (Exception ex) { setIpResults["cpuIpError"] = ex.Message; }
            }

            bool skipSubnet = (ctx.Request.QueryString["skipSubnet"] ?? "").ToLower() == "true";
            bool forceMove = (ctx.Request.QueryString["forceMove"] ?? "").ToLower() == "true";

            // Subnet 셋업 — 4 케이스 처리
            //  A) 둘 다 null      → subnet 새로 생성 후 양쪽 연결
            //  B) CPU O, HMI null → HMI 를 CPU subnet 에 연결
            //  C) CPU null, HMI O → CPU 를 HMI subnet 에 연결
            //  D) 둘 다 O         → 같은 subnet 이면 OK, 다르면 forceMove=true 일 때만 HMI 를 옮김
            string actualSubnetName = null;
            string subnetSetupNote = null;
            string preCpuSubnet = null, preHmiSubnet = null;
            try
            {
                Subnet cpuSubnet = null, hmiSubnet = null;
                try { cpuSubnet = cpuNode.ConnectedSubnet; preCpuSubnet = cpuSubnet?.Name; } catch { }
                try { hmiSubnet = hmiNode.ConnectedSubnet; preHmiSubnet = hmiSubnet?.Name; } catch { }

                if (skipSubnet)
                {
                    actualSubnetName = preCpuSubnet ?? preHmiSubnet;
                    subnetSetupNote = "skipSubnet=true — subnet 변경 없음 (CPU=" + (preCpuSubnet ?? "null") + ", HMI=" + (preHmiSubnet ?? "null") + ")";
                }
                else if (cpuSubnet == null && hmiSubnet == null)
                {
                    // A: 새로 생성 (subnet 이름에 '/' 같은 path 구분자 들어가면 V20 API 거부)
                    string newName = !string.IsNullOrEmpty(subnetName) ? subnetName : "PROFINET_IE_1";
                    if (newName.Contains("/") || newName.Contains("\\"))
                    {
                        SendJson(ctx.Response, 400, new
                        {
                            error = "Subnet 이름에 '/' 또는 '\\' 사용 불가: " + newName,
                            hint = "예: PROFINET_IE_1, Subnet_1, Ethernet_1"
                        });
                        return;
                    }
                    var newSubnet = cpuNode.CreateAndConnectToSubnet(newName);
                    actualSubnetName = newSubnet.Name;
                    hmiNode.ConnectToSubnet(newSubnet);
                    subnetSetupNote = "Subnet 새로 생성 + 양쪽 연결: " + actualSubnetName;
                }
                else if (cpuSubnet != null && hmiSubnet == null)
                {
                    // B: HMI 를 CPU subnet 에
                    actualSubnetName = cpuSubnet.Name;
                    hmiNode.ConnectToSubnet(cpuSubnet);
                    subnetSetupNote = "CPU 의 기존 subnet 재사용 + HMI 연결: " + actualSubnetName;
                }
                else if (cpuSubnet == null && hmiSubnet != null)
                {
                    // C: CPU 를 HMI subnet 에
                    actualSubnetName = hmiSubnet.Name;
                    cpuNode.ConnectToSubnet(hmiSubnet);
                    subnetSetupNote = "HMI 의 기존 subnet 재사용 + CPU 연결: " + actualSubnetName;
                }
                else
                {
                    // D: 둘 다 이미 연결됨
                    if (cpuSubnet.Name == hmiSubnet.Name)
                    {
                        actualSubnetName = cpuSubnet.Name;
                        subnetSetupNote = "양쪽 모두 같은 subnet 에 이미 연결됨: " + actualSubnetName;
                    }
                    else if (forceMove)
                    {
                        actualSubnetName = cpuSubnet.Name;
                        hmiNode.DisconnectFromSubnet();
                        hmiNode.ConnectToSubnet(cpuSubnet);
                        subnetSetupNote = "HMI 를 '" + hmiSubnet.Name + "' 에서 CPU subnet '" + cpuSubnet.Name + "' 로 이동 (forceMove)";
                    }
                    else
                    {
                        SendJson(ctx.Response, 409, new
                        {
                            error = "HMI 와 CPU 가 서로 다른 subnet 에 연결돼있음",
                            cpuSubnet = preCpuSubnet,
                            hmiSubnet = preHmiSubnet,
                            hint = "?forceMove=true 로 강제 이동, 또는 ?skipSubnet=true 로 subnet 단계 건너뛰고 connection 생성만 시도",
                            cpuInterfaceType = cpuNetIf.InterfaceType.ToString(),
                            hmiInterfaceType = hmiNetIf.InterfaceType.ToString()
                        });
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new
                {
                    error = "Subnet 연결 실패: " + ex.Message,
                    type = ex.GetType().Name,
                    stage = "subnet",
                    diagnostics = new
                    {
                        cpuSubnetBefore = preCpuSubnet,
                        hmiSubnetBefore = preHmiSubnet,
                        cpuInterfaceType = cpuNetIf?.InterfaceType.ToString(),
                        hmiInterfaceType = hmiNetIf?.InterfaceType.ToString(),
                        skipSubnetOption = "?skipSubnet=true 로 subnet 단계 건너뛰고 connection 생성만 시도 가능"
                    }
                });
                return;
            }

            // HMI Connection 생성 (옵션)
            // V20 ConnectionComposition 은 Create 메서드가 없어서 Import(XML) 만 가능.
            // 전략: 기존 connection 1개를 template 으로 export → Name/RemStAddress 수정 → Import
            object createdConnInfo = null;
            string createError = null;
            if (createConnection)
            {
                if (string.IsNullOrEmpty(connectionName)) connectionName = "HMI_Connection_X";

                var connComp = GetHmiConnections(hmi);
                if (connComp == null)
                {
                    createError = "HmiTarget.Connections 사용 불가";
                }
                else
                {
                    if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                    string templateFile = Path.Combine(tempDir, "conn_template_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".xml");
                    string templateSource = null;
                    try
                    {
                        // 1) 기존 connection 1개 export (있으면 template 으로 사용)
                        Connection existing = null;
                        foreach (Connection c in connComp) { existing = c; break; }
                        if (existing != null)
                        {
                            existing.Export(new FileInfo(templateFile), ExportOptions.WithDefaults);
                            templateSource = "기존 " + existing.Name;
                        }
                        else
                        {
                            // 2) 없으면 hardcoded template (S7_1500_OMS / Ethernet) 작성
                            File.WriteAllText(templateFile, BuildDefaultHmiConnectionXml(), Encoding.UTF8);
                            templateSource = "default S7-1500 Ethernet template";
                        }

                        // CPU IP 알아내기 (RemStAddress 로 사용)
                        string cpuIpEffective = cpuIp;
                        if (string.IsNullOrEmpty(cpuIpEffective))
                        {
                            try { cpuIpEffective = cpuNode.GetAttribute("Address") as string; } catch { }
                        }
                        // HMI IP (LocAddress)
                        string hmiIpEffective = hmiIp;
                        if (string.IsNullOrEmpty(hmiIpEffective))
                        {
                            try { hmiIpEffective = hmiNode.GetAttribute("Address") as string; } catch { }
                        }

                        var doc = XDocument.Load(templateFile);
                        var connEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Hmi.Communication.Connection");
                        if (connEl == null) throw new Exception("Template XML 파싱 실패: <Hmi.Communication.Connection> 없음");

                        var attrList = connEl.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
                        if (attrList != null)
                        {
                            var nameEl = attrList.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
                            if (nameEl != null) nameEl.Value = connectionName;
                            else attrList.Add(new XElement("Name", connectionName));
                        }

                        // ObjectList 안의 NameValuePair 에서 RemStAddress(CPU IP), LocAddress(HMI IP) 교체
                        var objList = connEl.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
                        if (objList != null)
                        {
                            foreach (var nvp in objList.Elements().Where(e => e.Name.LocalName == "Hmi.Communication.NameValuePair"))
                            {
                                var aL = nvp.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
                                if (aL == null) continue;
                                var keyEl = aL.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
                                if (keyEl == null) continue;
                                var valEl = aL.Elements().FirstOrDefault(e => e.Name.LocalName == "Value");
                                if (valEl == null) continue;

                                if (keyEl.Value == "RemStAddress" && !string.IsNullOrEmpty(cpuIpEffective))
                                    valEl.Value = cpuIpEffective;
                                else if (keyEl.Value == "LocAddress" && !string.IsNullOrEmpty(hmiIpEffective))
                                    valEl.Value = hmiIpEffective;
                            }
                        }

                        // 수정된 XML 을 새 파일로 저장 후 import
                        string importFile = Path.Combine(tempDir, "conn_import_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".xml");
                        doc.Save(importFile);
                        string shortPath = CopyToFlat(importFile, @"C:\T\hmiconngen");
                        File.Delete(importFile);
                        try
                        {
                            connComp.Import(new FileInfo(shortPath), ImportOptions.Override);
                        }
                        finally
                        {
                            if (File.Exists(shortPath)) File.Delete(shortPath);
                        }

                        Connection created = null;
                        try { created = connComp.Find(connectionName); } catch { }

                        // import 후 Partner / 기타 통합 속성 SetAttribute 시도 (V20 가 지원하는지 확인)
                        var partnerSetResults = new Dictionary<string, object>();
                        if (created != null)
                        {
                            var ieo = (IEngineeringObject)created;
                            // 시도할 attribute 이름 후보들 — V20 가 받아주는 게 있는지 시도
                            var partnerAttempts = new[]
                            {
                                new { key = "Partner", value = (object)cpuItem },
                                new { key = "PartnerDeviceItem", value = (object)cpuItem },
                                new { key = "PartnerStation", value = (object)cpuItem },
                                new { key = "TargetDevice", value = (object)cpuItem }
                            };
                            foreach (var att in partnerAttempts)
                            {
                                try
                                {
                                    ieo.SetAttribute(att.key, att.value);
                                    partnerSetResults[att.key] = "OK";
                                }
                                catch (Exception ex)
                                {
                                    var inner = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                                    partnerSetResults[att.key] = "fail: " + inner;
                                }
                            }
                        }

                        createdConnInfo = new
                        {
                            name = connectionName,
                            method = "Import (template clone)",
                            templateSource = templateSource,
                            locAddress = hmiIpEffective,
                            remStAddress = cpuIpEffective,
                            attributes = created != null ? ExtractHmiConnectionAttributes(created) : null,
                            partnerSetAttempts = partnerSetResults,
                            note = "V20 Openness 의 Connection 은 SetAttribute 로 Partner 설정이 거의 안 됨. RemStAddress (CPU IP) 만 설정되며, TIA Portal Connections 표의 Partner/Station/Node 컬럼은 비어있음. 통합 link 는 GUI 에서 수동 설정 필요."
                        };
                    }
                    catch (Exception ex)
                    {
                        var inner = ex.InnerException != null ? ex.InnerException.Message : null;
                        createError = "Connection import 실패: " + ex.Message + (inner != null ? " | inner: " + inner : "");
                    }
                    finally
                    {
                        if (File.Exists(templateFile)) try { File.Delete(templateFile); } catch { }
                    }
                }
            }

            SendJson(ctx.Response, 200, new
            {
                message = "Network 연결 완료" + (createdConnInfo != null ? " + Connection 생성됨" : ""),
                hmi = hmi.Name,
                cpu = cpuName,
                subnet = actualSubnetName,
                subnetSetup = subnetSetupNote,
                ipSetup = setIpResults,
                connectionCreated = createdConnInfo,
                connectionError = createError,
                hint = "TIA Portal Network view 에서 link 가 생성됐는지 + HMI 의 Connections 표에 partner 가 채워졌는지 확인하세요"
            });
        }

        // V20 ConnectionComposition.Import 로 사용할 기본 S7-1500 Ethernet HMI Connection XML.
        // 기존 connection 이 하나도 없을 때 fallback. Name 과 RemStAddress/LocAddress 는 호출 측에서 치환.
        static string BuildDefaultHmiConnectionXml()
        {
            return @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V20"" />
  <DocumentInfo>
    <Created>" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ") + @"</Created>
    <ExportSetting>WithDefaults</ExportSetting>
  </DocumentInfo>
  <Hmi.Communication.Connection ID=""0"">
    <AttributeList>
      <Driver>S7_1500_OMS</Driver>
      <InterfaceType>ETHERNET</InterfaceType>
      <Name>HMI_Connection_1</Name>
      <Online>true</Online>
      <PartnerIdForRuntime>-1</PartnerIdForRuntime>
      <PhysicId>S7_ETHERNET_IP4</PhysicId>
      <ProtocolId>S7_ETHERNET_IP4</ProtocolId>
      <ReceiveNcAlarms>None</ReceiveNcAlarms>
      <ReceiveNcMessages>false</ReceiveNcMessages>
      <ReceiveSimotionToAlarms>false</ReceiveSimotionToAlarms>
    </AttributeList>
    <ObjectList>
      <Hmi.Alarm.AlarmDisplayClass ID=""1"" CompositionName=""AlarmDisplayClasses""><AttributeList><Id>DisplayClass0</Id><Selected>true</Selected></AttributeList></Hmi.Alarm.AlarmDisplayClass>
      <Hmi.Alarm.AlarmDisplayClass ID=""2"" CompositionName=""AlarmDisplayClasses""><AttributeList><Id>DisplayClass1</Id><Selected>true</Selected></AttributeList></Hmi.Alarm.AlarmDisplayClass>
      <Hmi.Alarm.AlarmDisplayClass ID=""3"" CompositionName=""AlarmDisplayClasses""><AttributeList><Id>DisplayClass2</Id><Selected>true</Selected></AttributeList></Hmi.Alarm.AlarmDisplayClass>
      <Hmi.Alarm.AlarmDisplayClass ID=""4"" CompositionName=""AlarmDisplayClasses""><AttributeList><Id>DisplayClass3</Id><Selected>true</Selected></AttributeList></Hmi.Alarm.AlarmDisplayClass>
      <Hmi.Alarm.AlarmDisplayClass ID=""5"" CompositionName=""AlarmDisplayClasses""><AttributeList><Id>DisplayClass4</Id><Selected>true</Selected></AttributeList></Hmi.Alarm.AlarmDisplayClass>
      <Hmi.Alarm.AlarmDisplayClass ID=""6"" CompositionName=""AlarmDisplayClasses""><AttributeList><Id>DisplayClass5</Id><Selected>true</Selected></AttributeList></Hmi.Alarm.AlarmDisplayClass>
      <Hmi.Alarm.AlarmDisplayClass ID=""7"" CompositionName=""AlarmDisplayClasses""><AttributeList><Id>DisplayClass6</Id><Selected>true</Selected></AttributeList></Hmi.Alarm.AlarmDisplayClass>
      <Hmi.Alarm.AlarmDisplayClass ID=""8"" CompositionName=""AlarmDisplayClasses""><AttributeList><Id>DisplayClass7</Id><Selected>true</Selected></AttributeList></Hmi.Alarm.AlarmDisplayClass>
      <Hmi.Alarm.AlarmDisplayClass ID=""9"" CompositionName=""AlarmDisplayClasses""><AttributeList><Id>DisplayClass8</Id><Selected>true</Selected></AttributeList></Hmi.Alarm.AlarmDisplayClass>
      <Hmi.Alarm.AlarmDisplayClass ID=""A"" CompositionName=""AlarmDisplayClasses""><AttributeList><Id>DisplayClass9</Id><Selected>true</Selected></AttributeList></Hmi.Alarm.AlarmDisplayClass>
      <Hmi.Alarm.AlarmDisplayClass ID=""B"" CompositionName=""AlarmDisplayClasses""><AttributeList><Id>DisplayClass10</Id><Selected>true</Selected></AttributeList></Hmi.Alarm.AlarmDisplayClass>
      <Hmi.Alarm.AlarmDisplayClass ID=""C"" CompositionName=""AlarmDisplayClasses""><AttributeList><Id>DisplayClass11</Id><Selected>true</Selected></AttributeList></Hmi.Alarm.AlarmDisplayClass>
      <Hmi.Alarm.AlarmDisplayClass ID=""D"" CompositionName=""AlarmDisplayClasses""><AttributeList><Id>DisplayClass12</Id><Selected>true</Selected></AttributeList></Hmi.Alarm.AlarmDisplayClass>
      <Hmi.Alarm.AlarmDisplayClass ID=""E"" CompositionName=""AlarmDisplayClasses""><AttributeList><Id>DisplayClass13</Id><Selected>true</Selected></AttributeList></Hmi.Alarm.AlarmDisplayClass>
      <Hmi.Alarm.AlarmDisplayClass ID=""F"" CompositionName=""AlarmDisplayClasses""><AttributeList><Id>DisplayClass14</Id><Selected>true</Selected></AttributeList></Hmi.Alarm.AlarmDisplayClass>
      <Hmi.Alarm.AlarmDisplayClass ID=""10"" CompositionName=""AlarmDisplayClasses""><AttributeList><Id>DisplayClass15</Id><Selected>true</Selected></AttributeList></Hmi.Alarm.AlarmDisplayClass>
      <Hmi.Alarm.AlarmDisplayClass ID=""11"" CompositionName=""AlarmDisplayClasses""><AttributeList><Id>DisplayClass16</Id><Selected>true</Selected></AttributeList></Hmi.Alarm.AlarmDisplayClass>
      <Hmi.Communication.AreaPointer ID=""12"" CompositionName=""AreaPointers""><AttributeList><AreaPointerType>Coordination</AreaPointerType><LogicalAddress /><NameIndex>1</NameIndex></AttributeList><ObjectList><MultilingualText ID=""13"" CompositionName=""Comment""><ObjectList><MultilingualTextItem ID=""14"" CompositionName=""Items""><AttributeList><Culture>en-US</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></Hmi.Communication.AreaPointer>
      <Hmi.Communication.AreaPointer ID=""15"" CompositionName=""AreaPointers""><AttributeList><AreaPointerType>Coordination</AreaPointerType><LogicalAddress /><NameIndex>2</NameIndex></AttributeList><ObjectList><MultilingualText ID=""16"" CompositionName=""Comment""><ObjectList><MultilingualTextItem ID=""17"" CompositionName=""Items""><AttributeList><Culture>en-US</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></Hmi.Communication.AreaPointer>
      <Hmi.Communication.AreaPointer ID=""18"" CompositionName=""AreaPointers""><AttributeList><AreaPointerType>Coordination</AreaPointerType><LogicalAddress /><NameIndex>3</NameIndex></AttributeList><ObjectList><MultilingualText ID=""19"" CompositionName=""Comment""><ObjectList><MultilingualTextItem ID=""1A"" CompositionName=""Items""><AttributeList><Culture>en-US</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></Hmi.Communication.AreaPointer>
      <Hmi.Communication.AreaPointer ID=""1B"" CompositionName=""AreaPointers""><AttributeList><AreaPointerType>Coordination</AreaPointerType><LogicalAddress /><NameIndex>4</NameIndex></AttributeList><ObjectList><MultilingualText ID=""1C"" CompositionName=""Comment""><ObjectList><MultilingualTextItem ID=""1D"" CompositionName=""Items""><AttributeList><Culture>en-US</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></Hmi.Communication.AreaPointer>
      <Hmi.Communication.AreaPointer ID=""1E"" CompositionName=""AreaPointers""><AttributeList><AreaPointerType>Coordination</AreaPointerType><LogicalAddress /><NameIndex>5</NameIndex></AttributeList><ObjectList><MultilingualText ID=""1F"" CompositionName=""Comment""><ObjectList><MultilingualTextItem ID=""20"" CompositionName=""Items""><AttributeList><Culture>en-US</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></Hmi.Communication.AreaPointer>
      <Hmi.Communication.AreaPointer ID=""21"" CompositionName=""AreaPointers""><AttributeList><AreaPointerType>Coordination</AreaPointerType><LogicalAddress /><NameIndex>6</NameIndex></AttributeList><ObjectList><MultilingualText ID=""22"" CompositionName=""Comment""><ObjectList><MultilingualTextItem ID=""23"" CompositionName=""Items""><AttributeList><Culture>en-US</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></Hmi.Communication.AreaPointer>
      <Hmi.Communication.AreaPointer ID=""24"" CompositionName=""AreaPointers""><AttributeList><AreaPointerType>Coordination</AreaPointerType><LogicalAddress /><NameIndex>7</NameIndex></AttributeList><ObjectList><MultilingualText ID=""25"" CompositionName=""Comment""><ObjectList><MultilingualTextItem ID=""26"" CompositionName=""Items""><AttributeList><Culture>en-US</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></Hmi.Communication.AreaPointer>
      <Hmi.Communication.AreaPointer ID=""27"" CompositionName=""AreaPointers""><AttributeList><AreaPointerType>Coordination</AreaPointerType><LogicalAddress /><NameIndex>8</NameIndex></AttributeList><ObjectList><MultilingualText ID=""28"" CompositionName=""Comment""><ObjectList><MultilingualTextItem ID=""29"" CompositionName=""Items""><AttributeList><Culture>en-US</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></Hmi.Communication.AreaPointer>
      <Hmi.Communication.AreaPointer ID=""2A"" CompositionName=""AreaPointers""><AttributeList><AreaPointerType>DateTime</AreaPointerType><LogicalAddress /><NameIndex>1</NameIndex></AttributeList><ObjectList><MultilingualText ID=""2B"" CompositionName=""Comment""><ObjectList><MultilingualTextItem ID=""2C"" CompositionName=""Items""><AttributeList><Culture>en-US</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></Hmi.Communication.AreaPointer>
      <Hmi.Communication.AreaPointer ID=""2D"" CompositionName=""AreaPointers""><AttributeList><AreaPointerType>JobMailbox</AreaPointerType><LogicalAddress /><NameIndex>1</NameIndex></AttributeList><ObjectList><MultilingualText ID=""2E"" CompositionName=""Comment""><ObjectList><MultilingualTextItem ID=""2F"" CompositionName=""Items""><AttributeList><Culture>en-US</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></Hmi.Communication.AreaPointer>
      <Hmi.Communication.AreaPointer ID=""30"" CompositionName=""AreaPointers""><AttributeList><AreaPointerType>DataMailbox</AreaPointerType><LogicalAddress /><NameIndex>1</NameIndex></AttributeList><ObjectList><MultilingualText ID=""31"" CompositionName=""Comment""><ObjectList><MultilingualTextItem ID=""32"" CompositionName=""Items""><AttributeList><Culture>en-US</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></Hmi.Communication.AreaPointer>
      <MultilingualText ID=""33"" CompositionName=""Comment""><ObjectList><MultilingualTextItem ID=""34"" CompositionName=""Items""><AttributeList><Culture>en-US</Culture><Text /></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>
      <Hmi.Communication.NameValuePair ID=""35"" CompositionName=""PhysicValues""><AttributeList><Name>LocAddress</Name><Value>192.168.0.2</Value></AttributeList></Hmi.Communication.NameValuePair>
      <Hmi.Communication.NameValuePair ID=""36"" CompositionName=""PhysicValues""><AttributeList><Name>LocDeviceName</Name><Value>S7ONLINE</Value></AttributeList></Hmi.Communication.NameValuePair>
      <Hmi.Communication.NameValuePair ID=""37"" CompositionName=""ProtocolValues""><AttributeList><Name>TimeSyncMode</Name><Value>None</Value></AttributeList></Hmi.Communication.NameValuePair>
      <Hmi.Communication.NameValuePair ID=""38"" CompositionName=""ProtocolValues""><AttributeList><Name>RemStAddress</Name><Value>192.168.0.1</Value></AttributeList></Hmi.Communication.NameValuePair>
      <Hmi.Communication.NameValuePair ID=""39"" CompositionName=""ProtocolValues""><AttributeList><Name>Password</Name><Value /></AttributeList></Hmi.Communication.NameValuePair>
    </ObjectList>
  </Hmi.Communication.Connection>
</Document>";
        }

        static void CollectHmiTags(TagSystemFolder folder, string path, List<object> result, bool detailed)
        {
            foreach (TagTable table in folder.TagTables)
                result.Add(BuildHmiTagTableEntry(table, path, detailed));
            foreach (TagUserFolder uf in folder.Folders)
            {
                string subPath = string.IsNullOrEmpty(path) ? uf.Name : path + "/" + uf.Name;
                CollectHmiTagsUser(uf, subPath, result, detailed);
            }
        }

        static void CollectHmiTagsUser(TagUserFolder folder, string path, List<object> result, bool detailed)
        {
            foreach (TagTable table in folder.TagTables)
                result.Add(BuildHmiTagTableEntry(table, path, detailed));
            foreach (TagUserFolder uf in folder.Folders)
            {
                string subPath = path + "/" + uf.Name;
                CollectHmiTagsUser(uf, subPath, result, detailed);
            }
        }

        static object BuildHmiTagTableEntry(TagTable table, string path, bool detailed)
        {
            int tagCount = 0;
            try { tagCount = table.Tags.Count; } catch { }
            if (!detailed)
                return new { name = table.Name, path = path, tagCount = tagCount };
            var tags = ExtractHmiTagInfo(table);
            return new { name = table.Name, path = path, tagCount = tags.Count, tags = tags };
        }

        // V20 HMI Tag 는 reflection·IEngineeringObject.GetAttribute 채널 모두 막혀있어
        // 직접 프로퍼티 접근으로는 Name 외에는 아무 것도 못 읽는다.
        // 그러나 table.Export() XML 에는 모든 attribute 가 들어있으므로
        // 임시 export → XML 파싱 방식으로 attribute 를 추출한다.
        // 구조:
        //   <Hmi.Tag.Tag>
        //     <AttributeList> Length, LogicalAddress, Coding, Persistency, Scaling*, ... </AttributeList>
        //     <LinkList> Connection, DataType, ControllerTag, AcquisitionCycle, HmiDataType (각각 <Name>) </LinkList>
        //     <ObjectList> Comment / DisplayName / TagValue (MultilingualText) </ObjectList>
        //   </Hmi.Tag.Tag>
        static List<object> ExtractHmiTagInfo(TagTable table)
        {
            return ExtractHmiTagInfo(table, false);
        }

        static List<object> ExtractHmiTagInfo(TagTable table, bool debug)
        {
            var tags = new List<object>();
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            string tempFile = Path.Combine(tempDir, "hmitag_dump_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".xml");

            try
            {
                table.Export(new FileInfo(tempFile), ExportOptions.WithDefaults);
                if (!File.Exists(tempFile))
                {
                    if (debug) tags.Add(new Dictionary<string, object> { { "__error", "Export produced no file" } });
                    return tags;
                }

                var doc = XDocument.Load(tempFile);
                foreach (var tagEl in doc.Descendants().Where(e => e.Name.LocalName == "Hmi.Tag.Tag"))
                {
                    var attrs = new Dictionary<string, object>();

                    // AttributeList — simple key=value pairs
                    var attrList = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
                    if (attrList != null)
                    {
                        foreach (var a in attrList.Elements())
                        {
                            string val = a.Value;
                            attrs[a.Name.LocalName] = string.IsNullOrEmpty(val) ? null : (object)val;
                        }
                    }

                    // LinkList — reference objects with <Name>...</Name>
                    var linkList = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "LinkList");
                    if (linkList != null)
                    {
                        foreach (var link in linkList.Elements())
                        {
                            var nameEl = link.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
                            if (nameEl != null) attrs[link.Name.LocalName] = nameEl.Value;
                        }
                    }

                    // ObjectList → MultilingualText (Comment, DisplayName, TagValue 등)
                    // CompositionName 으로 키, 첫 번째 culture 의 Text 값으로 채움
                    var objList = tagEl.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
                    if (objList != null)
                    {
                        foreach (var ml in objList.Elements().Where(e => e.Name.LocalName == "MultilingualText"))
                        {
                            string compName = (string)ml.Attribute("CompositionName");
                            if (string.IsNullOrEmpty(compName)) continue;
                            if (attrs.ContainsKey(compName)) continue;

                            var firstItem = ml.Descendants().FirstOrDefault(e => e.Name.LocalName == "MultilingualTextItem");
                            if (firstItem != null)
                            {
                                var itemAttrList = firstItem.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
                                if (itemAttrList != null)
                                {
                                    var textEl = itemAttrList.Elements().FirstOrDefault(e => e.Name.LocalName == "Text");
                                    if (textEl != null)
                                    {
                                        attrs[compName] = string.IsNullOrEmpty(textEl.Value) ? null : (object)textEl.Value;
                                    }
                                }
                            }
                        }
                    }

                    if (debug)
                    {
                        attrs["__source"] = "TagTable.Export() XML";
                    }

                    tags.Add(attrs);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ExtractHmiTagInfo failed: " + ex.Message);
                if (debug) tags.Add(new Dictionary<string, object> { { "__error", ex.Message } });
            }
            finally
            {
                if (File.Exists(tempFile)) try { File.Delete(tempFile); } catch { }
            }

            return tags;
        }

        // GetAttribute 가 반환하는 값을 JSON 직렬화에 적합한 형태로 정규화.
        // 복합 객체(Connection 참조 등)는 ToString() 으로 떨어뜨려 직렬화 폭주 방지.
        static object NormalizeAttributeValue(object v)
        {
            if (v == null) return null;
            var t = v.GetType();
            if (t == typeof(string) || t.IsPrimitive || t.IsEnum || t == typeof(DateTime) || t == typeof(Guid))
                return v;
            // 다국어 텍스트 / 참조 객체 → ToString
            try { return v.ToString(); } catch { return null; }
        }

        // 폴더 경로(예: "Folder1/SubA")로 TagUserFolder 탐색. path 비어있으면 null 반환.
        static TagUserFolder NavigateHmiTagFolder(TagSystemFolder root, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string[] parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            TagUserFolder current = null;
            foreach (TagUserFolder uf in root.Folders)
                if (uf.Name == parts[0]) { current = uf; break; }
            if (current == null) return null;
            for (int i = 1; i < parts.Length; i++)
            {
                TagUserFolder next = null;
                foreach (TagUserFolder uf in current.Folders)
                    if (uf.Name == parts[i]) { next = uf; break; }
                if (next == null) return null;
                current = next;
            }
            return current;
        }

        // Import 시 폴더가 없으면 생성. path 비어있으면 null.
        static TagUserFolder NavigateOrCreateHmiTagFolder(TagSystemFolder root, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string[] parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            TagUserFolder current = null;
            foreach (TagUserFolder uf in root.Folders)
                if (uf.Name == parts[0]) { current = uf; break; }
            if (current == null)
            {
                try { current = root.Folders.Create(parts[0]); }
                catch { return null; }
            }
            for (int i = 1; i < parts.Length; i++)
            {
                TagUserFolder next = null;
                foreach (TagUserFolder uf in current.Folders)
                    if (uf.Name == parts[i]) { next = uf; break; }
                if (next == null)
                {
                    try { next = current.Folders.Create(parts[i]); }
                    catch { return null; }
                }
                current = next;
            }
            return current;
        }

        // 이름 + 폴더 경로로 TagTable 검색. folder 비어있으면 시스템 폴더 직속에서 찾고,
        // 없으면 사용자 폴더 전체를 재귀 탐색.
        static TagTable FindHmiTagTable(TagSystemFolder root, string folderPath, string tableName)
        {
            if (string.IsNullOrEmpty(folderPath))
            {
                foreach (TagTable t in root.TagTables)
                    if (t.Name == tableName) return t;
                foreach (TagUserFolder uf in root.Folders)
                {
                    var found = FindHmiTagTableInUser(uf, tableName);
                    if (found != null) return found;
                }
                return null;
            }
            var folder = NavigateHmiTagFolder(root, folderPath);
            if (folder == null) return null;
            foreach (TagTable t in folder.TagTables)
                if (t.Name == tableName) return t;
            return null;
        }

        static TagTable FindHmiTagTableInUser(TagUserFolder folder, string name)
        {
            foreach (TagTable t in folder.TagTables)
                if (t.Name == name) return t;
            foreach (TagUserFolder uf in folder.Folders)
            {
                var found = FindHmiTagTableInUser(uf, name);
                if (found != null) return found;
            }
            return null;
        }

        // ── HMI Tag handlers ──

        static void GetHmiTagTable(HttpListenerContext ctx, HmiTarget hmi)
        {
            string tableName = ctx.Request.QueryString["table"];
            string folderPath = ctx.Request.QueryString["folder"] ?? "";
            bool debug = ctx.Request.QueryString["debug"] == "true";
            if (string.IsNullOrEmpty(tableName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?table= parameter. Optional: ?folder=Folder1/SubA, ?debug=true (진단 정보 포함)" });
                return;
            }
            var table = FindHmiTagTable(hmi.TagFolder, folderPath, tableName);
            if (table == null)
            {
                SendJson(ctx.Response, 404, new { error = "Tag table not found: " + tableName, folder = folderPath });
                return;
            }
            var tags = ExtractHmiTagInfo(table, debug);
            SendJson(ctx.Response, 200, new { hmi = hmi.Name, table = table.Name, folder = folderPath, count = tags.Count, tags });
        }

        static void ExportHmiTagTable(HttpListenerContext ctx, HmiTarget hmi)
        {
            string tableName = ctx.Request.QueryString["table"];
            string folderPath = ctx.Request.QueryString["folder"] ?? "";
            if (string.IsNullOrEmpty(tableName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?table= parameter" });
                return;
            }
            var table = FindHmiTagTable(hmi.TagFolder, folderPath, tableName);
            if (table == null)
            {
                SendJson(ctx.Response, 404, new { error = "Tag table not found: " + tableName, folder = folderPath });
                return;
            }
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            string fileName = MakeValidFileName(table.Name) + ".xml";
            string tempFile = Path.Combine(tempDir, fileName);
            if (File.Exists(tempFile)) File.Delete(tempFile);
            try
            {
                table.Export(new FileInfo(tempFile), ExportOptions.WithDefaults);
                Console.WriteLine("HMI tag table exported: " + table.Name);
                SendFile(ctx.Response, tempFile, fileName);
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Tag table export failed: " + ex.Message, type = ex.GetType().Name });
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        static void ImportHmiTagTable(HttpListenerContext ctx, HmiTarget hmi)
        {
            string folderPath = ctx.Request.QueryString["folder"] ?? "";
            byte[] body;
            using (var ms = new MemoryStream())
            {
                ctx.Request.InputStream.CopyTo(ms);
                body = ms.ToArray();
            }
            if (body.Length == 0)
            {
                SendJson(ctx.Response, 400, new { error = "Empty request body. POST the tag table XML content." });
                return;
            }

            string tempFile = Path.Combine(tempDir, "hmi_tag_import_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".xml");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(tempFile, body);
            string shortPath = CopyToFlat(tempFile, @"C:\T\hmitagimport");
            File.Delete(tempFile);

            try
            {
                if (string.IsNullOrEmpty(folderPath))
                {
                    hmi.TagFolder.TagTables.Import(new FileInfo(shortPath), ImportOptions.Override);
                    SendJson(ctx.Response, 200, new { message = "HMI tag table imported", folder = "" });
                }
                else
                {
                    var folder = NavigateOrCreateHmiTagFolder(hmi.TagFolder, folderPath);
                    if (folder == null)
                    {
                        SendJson(ctx.Response, 500, new { error = "Failed to navigate/create folder: " + folderPath });
                        return;
                    }
                    folder.TagTables.Import(new FileInfo(shortPath), ImportOptions.Override);
                    SendJson(ctx.Response, 200, new { message = "HMI tag table imported", folder = folderPath });
                }
                Console.WriteLine("HMI tag table imported (folder=" + folderPath + ")");
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Tag table import failed: " + ex.Message, type = ex.GetType().Name });
            }
            finally
            {
                if (File.Exists(shortPath)) File.Delete(shortPath);
            }
        }

        static void ExportHmiTagsZip(HttpListenerResponse resp, HmiTarget hmi)
        {
            string exportDir = Path.Combine(tempDir, "hmi_tags_" + Guid.NewGuid().ToString().Substring(0, 8));
            Directory.CreateDirectory(exportDir);
            string zipFile = Path.Combine(tempDir, "hmi_tags_" + MakeValidFileName(hmi.Name) + ".zip");
            if (File.Exists(zipFile)) File.Delete(zipFile);

            int success = 0, failed = 0;
            try
            {
                using (var zip = ZipFile.Open(zipFile, ZipArchiveMode.Create))
                {
                    ExportHmiTagSystemFolderToZip(hmi.TagFolder, "", "HmiTags/", zip, exportDir, ref success, ref failed);
                }
                Console.WriteLine("HMI tag tables exported: " + success + " ok, " + failed + " failed");
                SendFileWithExportReport(resp, zipFile, "hmi_tags_" + MakeValidFileName(hmi.Name) + ".zip", success, failed);
            }
            catch (Exception ex)
            {
                SendJson(resp, 500, new { error = "HMI tags ZIP export failed: " + ex.Message });
            }
            finally
            {
                if (File.Exists(zipFile)) File.Delete(zipFile);
                if (Directory.Exists(exportDir)) Directory.Delete(exportDir, true);
            }
        }

        static void ExportHmiTagSystemFolderToZip(TagSystemFolder folder, string path, string zipPrefix,
            ZipArchive zip, string exportDir, ref int success, ref int failed)
        {
            foreach (TagTable table in folder.TagTables)
                ExportHmiTagTableEntryToZip(table, path, zipPrefix, zip, exportDir, ref success, ref failed);
            foreach (TagUserFolder sub in folder.Folders)
            {
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : path + "/" + sub.Name;
                ExportHmiTagUserFolderToZip(sub, subPath, zipPrefix, zip, exportDir, ref success, ref failed);
            }
        }

        static void ExportHmiTagUserFolderToZip(TagUserFolder folder, string path, string zipPrefix,
            ZipArchive zip, string exportDir, ref int success, ref int failed)
        {
            foreach (TagTable table in folder.TagTables)
                ExportHmiTagTableEntryToZip(table, path, zipPrefix, zip, exportDir, ref success, ref failed);
            foreach (TagUserFolder sub in folder.Folders)
            {
                string subPath = path + "/" + sub.Name;
                ExportHmiTagUserFolderToZip(sub, subPath, zipPrefix, zip, exportDir, ref success, ref failed);
            }
        }

        static void ExportHmiTagTableEntryToZip(TagTable table, string path, string zipPrefix,
            ZipArchive zip, string exportDir, ref int success, ref int failed)
        {
            string fileName = MakeValidFileName(table.Name) + ".xml";
            string tempFile = Path.Combine(exportDir, fileName);
            try
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
                table.Export(new FileInfo(tempFile), ExportOptions.WithDefaults);
                if (File.Exists(tempFile))
                {
                    string entryPath = string.IsNullOrEmpty(path)
                        ? zipPrefix + fileName
                        : zipPrefix + path + "/" + fileName;
                    zip.CreateEntryFromFile(tempFile, entryPath);
                    File.Delete(tempFile);
                    Console.WriteLine("  HMI Tag: " + entryPath);
                    success++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  HMI Tag export error [" + table.Name + "]: " + ex.Message);
                failed++;
            }
        }

        // ── HMI Screens ──

        static void GetHmiScreens(HttpListenerResponse resp, HmiTarget hmi)
        {
            var screens = new List<object>();
            CollectScreens(hmi.ScreenFolder, "", screens);

            var popups = new List<object>();
            CollectScreenPopups(hmi.ScreenPopupFolder, "", popups);

            var templates = new List<object>();
            CollectScreenTemplates(hmi.ScreenTemplateFolder, "", templates);

            SendJson(resp, 200, new {
                screenCount = screens.Count,
                screens,
                popupCount = popups.Count,
                popups,
                templateCount = templates.Count,
                templates
            });
        }

        static void CollectScreens(ScreenSystemFolder folder, string path, List<object> result)
        {
            foreach (Screen screen in folder.Screens)
            {
                result.Add(new { name = screen.Name, path, kind = "screen" });
            }
            foreach (ScreenUserFolder sub in folder.Folders)
            {
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : path + "/" + sub.Name;
                CollectScreensUser(sub, subPath, result);
            }
        }

        static void CollectScreensUser(ScreenUserFolder folder, string path, List<object> result)
        {
            foreach (Screen screen in folder.Screens)
            {
                result.Add(new { name = screen.Name, path, kind = "screen" });
            }
            foreach (ScreenUserFolder sub in folder.Folders)
            {
                string subPath = path + "/" + sub.Name;
                CollectScreensUser(sub, subPath, result);
            }
        }

        static void CollectScreenPopups(ScreenPopupSystemFolder folder, string path, List<object> result)
        {
            foreach (ScreenPopup popup in folder.ScreenPopups)
            {
                result.Add(new { name = popup.Name, path, kind = "popup" });
            }
            foreach (ScreenPopupUserFolder sub in folder.Folders)
            {
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : path + "/" + sub.Name;
                CollectScreenPopupsUser(sub, subPath, result);
            }
        }

        static void CollectScreenPopupsUser(ScreenPopupUserFolder folder, string path, List<object> result)
        {
            foreach (ScreenPopup popup in folder.ScreenPopups)
            {
                result.Add(new { name = popup.Name, path, kind = "popup" });
            }
            foreach (ScreenPopupUserFolder sub in folder.Folders)
            {
                string subPath = path + "/" + sub.Name;
                CollectScreenPopupsUser(sub, subPath, result);
            }
        }

        static void CollectScreenTemplates(ScreenTemplateSystemFolder folder, string path, List<object> result)
        {
            foreach (ScreenTemplate tmpl in folder.ScreenTemplates)
            {
                result.Add(new { name = tmpl.Name, path, kind = "template" });
            }
            foreach (ScreenTemplateUserFolder sub in folder.Folders)
            {
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : path + "/" + sub.Name;
                CollectScreenTemplatesUser(sub, subPath, result);
            }
        }

        static void CollectScreenTemplatesUser(ScreenTemplateUserFolder folder, string path, List<object> result)
        {
            foreach (ScreenTemplate tmpl in folder.ScreenTemplates)
            {
                result.Add(new { name = tmpl.Name, path, kind = "template" });
            }
            foreach (ScreenTemplateUserFolder sub in folder.Folders)
            {
                string subPath = path + "/" + sub.Name;
                CollectScreenTemplatesUser(sub, subPath, result);
            }
        }

        static void ExportHmiScreen(HttpListenerContext ctx, HmiTarget hmi)
        {
            string screenName = ctx.Request.QueryString["name"];
            string kind = (ctx.Request.QueryString["kind"] ?? "screen").ToLower();
            if (string.IsNullOrEmpty(screenName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?name= parameter. Optional: ?kind=screen|popup|template (default: screen)" });
                return;
            }

            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            string fileName = MakeValidFileName(screenName) + ".xml";
            string tempFile = Path.Combine(tempDir, fileName);
            if (File.Exists(tempFile)) File.Delete(tempFile);

            try
            {
                if (kind == "popup")
                {
                    var popup = FindScreenPopup(hmi.ScreenPopupFolder, screenName);
                    if (popup == null) { SendJson(ctx.Response, 404, new { error = "Popup not found: " + screenName }); return; }
                    popup.Export(new FileInfo(tempFile), ExportOptions.WithDefaults);
                }
                else if (kind == "template")
                {
                    var tmpl = FindScreenTemplate(hmi.ScreenTemplateFolder, screenName);
                    if (tmpl == null) { SendJson(ctx.Response, 404, new { error = "Template not found: " + screenName }); return; }
                    tmpl.Export(new FileInfo(tempFile), ExportOptions.WithDefaults);
                }
                else
                {
                    var screen = FindScreen(hmi.ScreenFolder, screenName);
                    if (screen == null) { SendJson(ctx.Response, 404, new { error = "Screen not found: " + screenName }); return; }
                    screen.Export(new FileInfo(tempFile), ExportOptions.WithDefaults);
                }

                Console.WriteLine("HMI screen exported: " + screenName + " (" + kind + ")");
                SendFile(ctx.Response, tempFile, fileName);
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Screen export failed: " + ex.Message, type = ex.GetType().Name });
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        static void ImportHmiScreen(HttpListenerContext ctx, HmiTarget hmi)
        {
            string kind = (ctx.Request.QueryString["kind"] ?? "screen").ToLower();
            string folderPath = ctx.Request.QueryString["folder"] ?? "";

            byte[] body;
            using (var ms = new MemoryStream())
            {
                ctx.Request.InputStream.CopyTo(ms);
                body = ms.ToArray();
            }
            if (body.Length == 0)
            {
                SendJson(ctx.Response, 400, new { error = "Empty request body. POST the screen XML content." });
                return;
            }

            string tempFile = Path.Combine(tempDir, "hmi_import_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".xml");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(tempFile, body);

            string shortPath = CopyToFlat(tempFile, @"C:\T\hmiimport");
            File.Delete(tempFile);

            try
            {
                if (kind == "popup")
                {
                    hmi.ScreenPopupFolder.ScreenPopups.Import(new FileInfo(shortPath), ImportOptions.Override);
                    SendJson(ctx.Response, 200, new { message = "Screen popup imported", kind });
                }
                else if (kind == "template")
                {
                    hmi.ScreenTemplateFolder.ScreenTemplates.Import(new FileInfo(shortPath), ImportOptions.Override);
                    SendJson(ctx.Response, 200, new { message = "Screen template imported", kind });
                }
                else
                {
                    if (string.IsNullOrEmpty(folderPath))
                    {
                        hmi.ScreenFolder.Screens.Import(new FileInfo(shortPath), ImportOptions.Override);
                    }
                    else
                    {
                        var targetFolder = NavigateScreenFolder(hmi.ScreenFolder, folderPath);
                        if (targetFolder != null)
                            targetFolder.Screens.Import(new FileInfo(shortPath), ImportOptions.Override);
                        else
                            hmi.ScreenFolder.Screens.Import(new FileInfo(shortPath), ImportOptions.Override);
                    }
                    SendJson(ctx.Response, 200, new { message = "Screen imported", kind, folder = folderPath });
                }
                Console.WriteLine("HMI screen imported (" + kind + ")");
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Screen import failed: " + ex.Message, type = ex.GetType().Name });
            }
            finally
            {
                if (File.Exists(shortPath)) File.Delete(shortPath);
            }
        }

        static void ExportHmiScreensZip(HttpListenerResponse resp, HmiTarget hmi)
        {
            string exportDir = Path.Combine(tempDir, "hmi_export_" + Guid.NewGuid().ToString().Substring(0, 8));
            Directory.CreateDirectory(exportDir);
            string zipFile = Path.Combine(tempDir, "hmi_screens.zip");
            if (File.Exists(zipFile)) File.Delete(zipFile);

            int success = 0, failed = 0;

            try
            {
                using (var zip = ZipFile.Open(zipFile, ZipArchiveMode.Create))
                {
                    // Screens
                    ExportScreenFolderToZip(hmi.ScreenFolder, "", "Screens/", zip, exportDir, ref success, ref failed);

                    // Popups
                    ExportScreenPopupFolderToZip(hmi.ScreenPopupFolder, "", "Popups/", zip, exportDir, ref success, ref failed);

                    // Templates
                    ExportScreenTemplateFolderToZip(hmi.ScreenTemplateFolder, "", "Templates/", zip, exportDir, ref success, ref failed);
                }

                Console.WriteLine("HMI screens exported: " + success + " ok, " + failed + " failed");
                SendFileWithExportReport(resp, zipFile, "hmi_screens.zip", success, failed);
            }
            catch (Exception ex)
            {
                SendJson(resp, 500, new { error = "HMI screens ZIP export failed: " + ex.Message });
            }
            finally
            {
                if (File.Exists(zipFile)) File.Delete(zipFile);
                if (Directory.Exists(exportDir)) Directory.Delete(exportDir, true);
            }
        }

        static void ExportScreenFolderToZip(ScreenSystemFolder folder, string path, string zipPrefix,
            ZipArchive zip, string exportDir, ref int success, ref int failed)
        {
            foreach (Screen screen in folder.Screens)
            {
                ExportScreenItemToZip(screen, screen.Name, path, zipPrefix, zip, exportDir, ref success, ref failed);
            }
            foreach (ScreenUserFolder sub in folder.Folders)
            {
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : path + "/" + sub.Name;
                ExportScreenUserFolderToZip(sub, subPath, zipPrefix, zip, exportDir, ref success, ref failed);
            }
        }

        static void ExportScreenUserFolderToZip(ScreenUserFolder folder, string path, string zipPrefix,
            ZipArchive zip, string exportDir, ref int success, ref int failed)
        {
            foreach (Screen screen in folder.Screens)
            {
                ExportScreenItemToZip(screen, screen.Name, path, zipPrefix, zip, exportDir, ref success, ref failed);
            }
            foreach (ScreenUserFolder sub in folder.Folders)
            {
                string subPath = path + "/" + sub.Name;
                ExportScreenUserFolderToZip(sub, subPath, zipPrefix, zip, exportDir, ref success, ref failed);
            }
        }

        static void ExportScreenPopupFolderToZip(ScreenPopupSystemFolder folder, string path, string zipPrefix,
            ZipArchive zip, string exportDir, ref int success, ref int failed)
        {
            foreach (ScreenPopup popup in folder.ScreenPopups)
            {
                ExportScreenItemToZip(popup, popup.Name, path, zipPrefix, zip, exportDir, ref success, ref failed);
            }
            foreach (ScreenPopupUserFolder sub in folder.Folders)
            {
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : path + "/" + sub.Name;
                ExportScreenPopupUserFolderToZip(sub, subPath, zipPrefix, zip, exportDir, ref success, ref failed);
            }
        }

        static void ExportScreenPopupUserFolderToZip(ScreenPopupUserFolder folder, string path, string zipPrefix,
            ZipArchive zip, string exportDir, ref int success, ref int failed)
        {
            foreach (ScreenPopup popup in folder.ScreenPopups)
            {
                ExportScreenItemToZip(popup, popup.Name, path, zipPrefix, zip, exportDir, ref success, ref failed);
            }
            foreach (ScreenPopupUserFolder sub in folder.Folders)
            {
                string subPath = path + "/" + sub.Name;
                ExportScreenPopupUserFolderToZip(sub, subPath, zipPrefix, zip, exportDir, ref success, ref failed);
            }
        }

        static void ExportScreenTemplateFolderToZip(ScreenTemplateSystemFolder folder, string path, string zipPrefix,
            ZipArchive zip, string exportDir, ref int success, ref int failed)
        {
            foreach (ScreenTemplate tmpl in folder.ScreenTemplates)
            {
                ExportScreenItemToZip(tmpl, tmpl.Name, path, zipPrefix, zip, exportDir, ref success, ref failed);
            }
            foreach (ScreenTemplateUserFolder sub in folder.Folders)
            {
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : path + "/" + sub.Name;
                ExportScreenTemplateUserFolderToZip(sub, subPath, zipPrefix, zip, exportDir, ref success, ref failed);
            }
        }

        static void ExportScreenTemplateUserFolderToZip(ScreenTemplateUserFolder folder, string path, string zipPrefix,
            ZipArchive zip, string exportDir, ref int success, ref int failed)
        {
            foreach (ScreenTemplate tmpl in folder.ScreenTemplates)
            {
                ExportScreenItemToZip(tmpl, tmpl.Name, path, zipPrefix, zip, exportDir, ref success, ref failed);
            }
            foreach (ScreenTemplateUserFolder sub in folder.Folders)
            {
                string subPath = path + "/" + sub.Name;
                ExportScreenTemplateUserFolderToZip(sub, subPath, zipPrefix, zip, exportDir, ref success, ref failed);
            }
        }

        static void ExportScreenItemToZip(IEngineeringObject item, string name, string path, string zipPrefix,
            ZipArchive zip, string exportDir, ref int success, ref int failed)
        {
            string fileName = MakeValidFileName(name) + ".xml";
            string tempFile = Path.Combine(exportDir, fileName);
            try
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);

                // Export via reflection (Screen, ScreenPopup, ScreenTemplate all have Export method)
                var exportMethod = item.GetType().GetMethod("Export", new[] { typeof(FileInfo), typeof(ExportOptions) });
                if (exportMethod != null)
                {
                    exportMethod.Invoke(item, new object[] { new FileInfo(tempFile), ExportOptions.WithDefaults });
                }

                if (File.Exists(tempFile))
                {
                    string entryPath = string.IsNullOrEmpty(path)
                        ? zipPrefix + fileName
                        : zipPrefix + path + "/" + fileName;
                    zip.CreateEntryFromFile(tempFile, entryPath);
                    File.Delete(tempFile);
                    Console.WriteLine("  HMI: " + entryPath);
                    success++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  HMI export error [" + name + "]: " + ex.Message);
                failed++;
            }
        }

        static Screen FindScreen(ScreenSystemFolder folder, string name)
        {
            foreach (Screen s in folder.Screens)
                if (s.Name == name) return s;
            foreach (ScreenUserFolder sub in folder.Folders)
            {
                var found = FindScreenInUser(sub, name);
                if (found != null) return found;
            }
            return null;
        }

        static Screen FindScreenInUser(ScreenUserFolder folder, string name)
        {
            foreach (Screen s in folder.Screens)
                if (s.Name == name) return s;
            foreach (ScreenUserFolder sub in folder.Folders)
            {
                var found = FindScreenInUser(sub, name);
                if (found != null) return found;
            }
            return null;
        }

        static ScreenPopup FindScreenPopup(ScreenPopupSystemFolder folder, string name)
        {
            foreach (ScreenPopup p in folder.ScreenPopups)
                if (p.Name == name) return p;
            foreach (ScreenPopupUserFolder sub in folder.Folders)
            {
                var found = FindScreenPopupInUser(sub, name);
                if (found != null) return found;
            }
            return null;
        }

        static ScreenPopup FindScreenPopupInUser(ScreenPopupUserFolder folder, string name)
        {
            foreach (ScreenPopup p in folder.ScreenPopups)
                if (p.Name == name) return p;
            foreach (ScreenPopupUserFolder sub in folder.Folders)
            {
                var found = FindScreenPopupInUser(sub, name);
                if (found != null) return found;
            }
            return null;
        }

        static ScreenTemplate FindScreenTemplate(ScreenTemplateSystemFolder folder, string name)
        {
            foreach (ScreenTemplate t in folder.ScreenTemplates)
                if (t.Name == name) return t;
            foreach (ScreenTemplateUserFolder sub in folder.Folders)
            {
                var found = FindScreenTemplateInUser(sub, name);
                if (found != null) return found;
            }
            return null;
        }

        static ScreenTemplate FindScreenTemplateInUser(ScreenTemplateUserFolder folder, string name)
        {
            foreach (ScreenTemplate t in folder.ScreenTemplates)
                if (t.Name == name) return t;
            foreach (ScreenTemplateUserFolder sub in folder.Folders)
            {
                var found = FindScreenTemplateInUser(sub, name);
                if (found != null) return found;
            }
            return null;
        }

        static ScreenUserFolder NavigateScreenFolder(ScreenSystemFolder root, string path)
        {
            string[] parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            ScreenUserFolder current = null;

            foreach (ScreenUserFolder sub in root.Folders)
            {
                if (sub.Name == parts[0]) { current = sub; break; }
            }
            if (current == null) return null;

            for (int i = 1; i < parts.Length; i++)
            {
                ScreenUserFolder next = null;
                foreach (ScreenUserFolder sub in current.Folders)
                {
                    if (sub.Name == parts[i]) { next = sub; break; }
                }
                if (next == null) return null;
                current = next;
            }
            return current;
        }

        // ── Hardware Catalog Search ──

        static void SearchCatalog(HttpListenerContext ctx)
        {
            string search = ctx.Request.QueryString["q"];
            if (string.IsNullOrEmpty(search))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?q= search term (e.g. q=1FK2)" });
                return;
            }
            EnsureConnected();

            var catalog = connectedPortal.HardwareCatalog;
            var entries = catalog.Find(search);
            var results = new List<object>();

            foreach (Siemens.Engineering.HW.HardwareCatalog.CatalogEntry entry in entries)
            {
                results.Add(new {
                    articleNumber = entry.ArticleNumber,
                    typeIdentifier = entry.TypeIdentifier,
                    typeName = entry.TypeName,
                    description = entry.Description,
                    version = entry.Version,
                    catalogPath = entry.CatalogPath
                });
            }

            SendJson(ctx.Response, 200, new { search, count = results.Count, entries = results });
        }

        static void SearchCompatible(HttpListenerContext ctx)
        {
            string search = ctx.Request.QueryString["q"] ?? "";
            string typeId = ctx.Request.QueryString["type"];
            if (string.IsNullOrEmpty(typeId))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?type= TypeIdentifier of the target device (e.g. type=OrderNumber:6SL3210-5HE10-4xFx/V5.2.3/S210). Use ?q= for search filter (optional)." });
                return;
            }
            EnsureConnected();

            var catalog = connectedPortal.HardwareCatalog;
            var entries = catalog.Find(search, typeId);
            var results = new List<object>();

            foreach (Siemens.Engineering.HW.HardwareCatalog.CatalogEntry entry in entries)
            {
                results.Add(new {
                    articleNumber = entry.ArticleNumber,
                    typeIdentifier = entry.TypeIdentifier,
                    typeName = entry.TypeName,
                    description = entry.Description,
                    version = entry.Version,
                    catalogPath = entry.CatalogPath
                });
            }

            SendJson(ctx.Response, 200, new { search, typeFilter = typeId, count = results.Count, entries = results });
        }

        // ── Device Item Exploration ──

        static void GetDeviceItems(HttpListenerContext ctx)
        {
            string deviceName = ctx.Request.QueryString["device"];
            string projectName = ctx.Request.QueryString["project"];
            EnsureConnected();

            Device targetDevice = null;
            foreach (Project proj in connectedPortal.Projects)
            {
                if (projectName != null && proj.Name != projectName) continue;
                foreach (Device dev in EnumerateAllDevices(proj))
                {
                    if (dev.Name == deviceName) { targetDevice = dev; break; }
                }
                if (targetDevice != null) break;
            }
            if (targetDevice == null) { SendJson(ctx.Response, 404, new { error = "Device not found: " + deviceName }); return; }

            var items = CollectDeviceItems(targetDevice.DeviceItems, 0);
            SendJson(ctx.Response, 200, new { device = targetDevice.Name, count = items.Count, items });
        }

        // ── 디바이스/아이템 하위의 모든 attribute 이름을 재귀 탐색하고 패턴으로 필터 ──
        // GET /api/device/search-attrs?device=&item=&project=&pattern=Diagnostic|WireBreak&maxDepth=6
        // 출력: 매칭된 attribute 의 전체 경로 (path · attrName · value · accessMode · type)
        static void SearchDeviceAttributes(HttpListenerContext ctx)
        {
            EnsureConnected();
            string deviceName = ctx.Request.QueryString["device"];
            string itemPath = ctx.Request.QueryString["item"];
            string projectName = ctx.Request.QueryString["project"];
            string pattern = ctx.Request.QueryString["pattern"];
            int maxDepth = 6;
            try { int.TryParse(ctx.Request.QueryString["maxDepth"] ?? "6", out maxDepth); } catch { }

            if (string.IsNullOrEmpty(deviceName) || string.IsNullOrEmpty(pattern))
            {
                SendJson(ctx.Response, 400, new { error = "?device= 와 ?pattern= 필요. 예: ?device=IO%20device_1&pattern=Diagnostic|WireBreak" });
                return;
            }

            System.Text.RegularExpressions.Regex regex;
            try { regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
            catch (Exception ex) { SendJson(ctx.Response, 400, new { error = "정규식 컴파일 실패: " + ex.Message }); return; }

            // 시작 객체: item 이 비어있으면 Device 의 모든 top-level DeviceItem
            var matches = new List<object>();
            int scannedItems = 0, scannedAttrs = 0;

            if (string.IsNullOrEmpty(itemPath))
            {
                Device targetDevice = null;
                foreach (Project proj in connectedPortal.Projects)
                {
                    if (projectName != null && proj.Name != projectName) continue;
                    foreach (Device d in EnumerateAllDevices(proj))
                    {
                        if (d.Name == deviceName) { targetDevice = d; break; }
                    }
                    if (targetDevice != null) break;
                }
                if (targetDevice == null) { SendJson(ctx.Response, 404, new { error = "Device not found: " + deviceName }); return; }
                foreach (DeviceItem di in targetDevice.DeviceItems)
                    SearchInDeviceItemRecursive(di, deviceName + "/" + di.Name, regex, matches, ref scannedItems, ref scannedAttrs, 0, maxDepth);
            }
            else
            {
                var di = FindDeviceItemByPath(deviceName, itemPath, projectName);
                if (di == null) { SendJson(ctx.Response, 404, new { error = "DeviceItem not found: " + itemPath }); return; }
                SearchInDeviceItemRecursive(di, deviceName + "/" + itemPath, regex, matches, ref scannedItems, ref scannedAttrs, 0, maxDepth);
            }

            SendJson(ctx.Response, 200, new {
                pattern,
                maxDepth,
                scannedItems,
                scannedAttrs,
                matchCount = matches.Count,
                matches
            });
        }

        static void SearchInDeviceItemRecursive(DeviceItem di, string path, System.Text.RegularExpressions.Regex regex,
            List<object> matches, ref int scannedItems, ref int scannedAttrs, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;
            scannedItems++;

            // 1) DeviceItem 자체의 attribute
            try
            {
                var ieo = (IEngineeringObject)di;
                foreach (var info in ieo.GetAttributeInfos())
                {
                    scannedAttrs++;
                    if (!regex.IsMatch(info.Name)) continue;
                    string val = null;
                    try { var v = ieo.GetAttribute(info.Name); val = v != null ? v.ToString() : null; } catch { }
                    matches.Add(new {
                        path = path,
                        attribute = info.Name,
                        value = val,
                        target = "",
                        location = "DeviceItem"
                    });
                }
            }
            catch { }

            // 2) GetCompositionInfos 로 DeviceItems 외 hidden composition 의 각 아이템 attribute
            try
            {
                var ieo = (IEngineeringObject)di;
                foreach (var compInfo in ieo.GetCompositionInfos())
                {
                    string compName = null;
                    try { compName = compInfo.GetType().GetProperty("Name")?.GetValue(compInfo, null) as string; } catch { }
                    if (string.IsNullOrEmpty(compName) || compName == "DeviceItems") continue;

                    object comp = null;
                    try { comp = ieo.GetComposition(compName); } catch { }
                    var enumComp = comp as System.Collections.IEnumerable;
                    if (enumComp == null) continue;

                    int idx = 0;
                    foreach (var item in enumComp)
                    {
                        if (!(item is IEngineeringObject subIeo)) { idx++; continue; }
                        scannedItems++;
                        string itemName = null;
                        try { itemName = subIeo.GetAttribute("Name") as string; } catch { }
                        if (string.IsNullOrEmpty(itemName)) itemName = "[" + idx + "]";

                        try
                        {
                            foreach (var info in subIeo.GetAttributeInfos())
                            {
                                scannedAttrs++;
                                if (!regex.IsMatch(info.Name)) continue;
                                string val = null;
                                try { var v = subIeo.GetAttribute(info.Name); val = v != null ? v.ToString() : null; } catch { }
                                matches.Add(new {
                                    path = path + " > " + compName + "[" + idx + "] (" + itemName + ")",
                                    attribute = info.Name,
                                    value = val,
                                    target = compName + "[" + idx + "]",
                                    location = "Composition"
                                });
                            }
                        }
                        catch { }
                        idx++;
                    }
                }
            }
            catch { }

            // 3) NetworkInterface 서비스 (IoConnectors / IoControllers)
            try
            {
                var netIf = ((IEngineeringServiceProvider)di).GetService<NetworkInterface>();
                if (netIf != null)
                {
                    try
                    {
                        int idx = 0;
                        foreach (IoConnector ioc in netIf.IoConnectors)
                        {
                            scannedItems++;
                            foreach (var info in ((IEngineeringObject)ioc).GetAttributeInfos())
                            {
                                scannedAttrs++;
                                if (!regex.IsMatch(info.Name)) continue;
                                string val = null;
                                try { var v = ((IEngineeringObject)ioc).GetAttribute(info.Name); val = v != null ? v.ToString() : null; } catch { }
                                matches.Add(new { path = path + " > IoConnector[" + idx + "]", attribute = info.Name, value = val, target = "IoConnector[" + idx + "]", location = "NetworkInterface.IoConnectors" });
                            }
                            idx++;
                        }
                    }
                    catch { }
                    try
                    {
                        int idx = 0;
                        foreach (var ioc in netIf.IoControllers)
                        {
                            scannedItems++;
                            foreach (var info in ((IEngineeringObject)ioc).GetAttributeInfos())
                            {
                                scannedAttrs++;
                                if (!regex.IsMatch(info.Name)) continue;
                                string val = null;
                                try { var v = ((IEngineeringObject)ioc).GetAttribute(info.Name); val = v != null ? v.ToString() : null; } catch { }
                                matches.Add(new { path = path + " > IoController[" + idx + "]", attribute = info.Name, value = val, target = "IoController[" + idx + "]", location = "NetworkInterface.IoControllers" });
                            }
                            idx++;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // 3b) MRP feature 도 search 에서 제외 (TIA 크래시 위험)

            // 4) 재귀: 자식 DeviceItem
            try
            {
                foreach (DeviceItem child in di.DeviceItems)
                    SearchInDeviceItemRecursive(child, path + "/" + child.Name, regex, matches, ref scannedItems, ref scannedAttrs, depth + 1, maxDepth);
            }
            catch { }

            // 5) DeviceItem.GetAttribute("Items") → DeviceItemAssociation 도 walking
            //    (HF 모듈의 채널/submodule 이 여기 있을 가능성)
            try
            {
                var itemsObj = ((IEngineeringObject)di).GetAttribute("Items");
                if (itemsObj is System.Collections.IEnumerable assoc)
                {
                    int idx = 0;
                    foreach (var item in assoc)
                    {
                        if (item is DeviceItem subDi)
                        {
                            SearchInDeviceItemRecursive(subDi, path + " > Items[" + idx + "](" + (subDi.Name ?? "?") + ")", regex, matches, ref scannedItems, ref scannedAttrs, depth + 1, maxDepth);
                        }
                        idx++;
                    }
                }
            }
            catch { }
        }

        // GET /api/device/_debug?device=&itemName=
        // Device + 그 하위 모든 DeviceItem 트리에서 hidden composition / invocation 탐색.
        // ?itemName= 으로 특정 DeviceItem 만 좁힐 수도 있음 (예: PLC_1, "PROFINET interface_1")
        static void DumpDevice(HttpListenerContext ctx)
        {
            string deviceName = ctx.Request.QueryString["device"];
            string itemName = ctx.Request.QueryString["item"];
            EnsureConnected();
            if (string.IsNullOrEmpty(deviceName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?device= parameter" });
                return;
            }

            Device targetDevice = null;
            string matchedBy = null;
            // 1) Device 이름으로 검색
            foreach (Project proj in connectedPortal.Projects)
            {
                foreach (Device dev in proj.Devices)
                {
                    if (dev.Name == deviceName) { targetDevice = dev; matchedBy = "Device.Name"; break; }
                }
                if (targetDevice != null) break;
            }
            // 2) DeviceItem 이름으로 fallback 검색 (예: "PLC_1" 은 DeviceItem 이름이고 Device 이름은 "S7-1500/... station_1")
            if (targetDevice == null)
            {
                foreach (Project proj in connectedPortal.Projects)
                {
                    foreach (Device dev in proj.Devices)
                    {
                        foreach (DeviceItem di in dev.DeviceItems)
                        {
                            if (FindDeviceItemNamedRecursive(di, deviceName) != null)
                            {
                                targetDevice = dev;
                                matchedBy = "DeviceItem.Name (under Device " + dev.Name + ")";
                                break;
                            }
                        }
                        if (targetDevice != null) break;
                    }
                    if (targetDevice != null) break;
                }
            }
            if (targetDevice == null)
            {
                // 후보 디바이스/아이템 목록 같이 반환
                var allDevices = new List<object>();
                foreach (Project proj in connectedPortal.Projects)
                {
                    foreach (Device dev in proj.Devices)
                    {
                        var diNames = new List<string>();
                        foreach (DeviceItem di in dev.DeviceItems)
                        {
                            CollectDeviceItemNames(di, diNames);
                        }
                        allDevices.Add(new { project = proj.Name, deviceName = dev.Name, items = diNames });
                    }
                }
                SendJson(ctx.Response, 404, new { error = "Device 또는 DeviceItem not found: " + deviceName, available = allDevices });
                return;
            }

            var result = new Dictionary<string, object>();
            result["matchedBy"] = matchedBy;
            result["device"] = DumpEngineeringObject(targetDevice, "Device", maxDepth: 1);

            // 모든 DeviceItem 트리 dump (item 지정 시 그것만)
            var itemDumps = new List<object>();
            DumpDeviceItemsRecursive(targetDevice.DeviceItems, itemName, itemDumps, 0);
            result["deviceItems"] = itemDumps;

            // PlcSoftware 가 있으면 그것도 dump
            DeviceItem cpuItem = null;
            foreach (DeviceItem di in targetDevice.DeviceItems)
            {
                cpuItem = FindCpuRecursive(di);
                if (cpuItem != null) break;
            }
            if (cpuItem != null)
            {
                var sc = ((IEngineeringServiceProvider)cpuItem).GetService<SoftwareContainer>();
                if (sc != null && sc.Software is PlcSoftware plc)
                {
                    result["plcSoftware"] = DumpEngineeringObject(plc, "PlcSoftware", maxDepth: 1);
                }
            }

            SendJson(ctx.Response, 200, result);
        }

        // GET /api/project/_debug?project=
        // Project root 의 모든 hidden composition / invocation / typed property dump.
        // 만약 integrated connection 데이터가 project 레벨에 있다면 여기서 잡힘.
        static void DumpProject(HttpListenerContext ctx)
        {
            string projectName = ctx.Request.QueryString["project"];
            EnsureConnected();
            Project project = null;
            foreach (Project p in connectedPortal.Projects)
            {
                if (projectName == null || p.Name == projectName) { project = p; break; }
            }
            if (project == null)
            {
                SendJson(ctx.Response, 404, new { error = "Project not found" });
                return;
            }

            var result = new Dictionary<string, object>();
            result["project"] = DumpEngineeringObject(project, "Project", maxDepth: 1);

            // 각 subnet 도 같이 dump
            var subnetDumps = new List<object>();
            foreach (var subnet in project.Subnets)
            {
                subnetDumps.Add(DumpEngineeringObject(subnet, "Subnet", maxDepth: 1));
            }
            result["subnets"] = subnetDumps;

            // GetServiceInfos 시도 (Project 가 IEngineeringServiceProvider 를 구현하면)
            var services = new List<string>();
            try
            {
                var sip = project as IEngineeringServiceProvider;
                if (sip != null)
                {
                    foreach (var info in sip.GetServiceInfos())
                    {
                        string svcType = null;
                        try { svcType = info.GetType().GetProperty("Type")?.GetValue(info)?.ToString(); } catch { }
                        services.Add(svcType ?? info.GetType().FullName);
                    }
                }
            }
            catch (Exception ex) { result["serviceError"] = ex.Message; }
            result["services"] = services;

            SendJson(ctx.Response, 200, result);
        }

        // GET /api/subnet/_debug?subnet=
        // Subnet 의 모든 hidden composition / invocation / 노드별 hidden composition dump.
        // S7 connection 같은 게 subnet 레벨에 hidden composition 으로 있는지 확인.
        static void DumpSubnet(HttpListenerContext ctx)
        {
            string subnetName = ctx.Request.QueryString["subnet"];
            EnsureConnected();
            if (string.IsNullOrEmpty(subnetName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?subnet= parameter" });
                return;
            }
            Subnet target = null;
            foreach (Project p in connectedPortal.Projects)
            {
                foreach (Subnet sn in p.Subnets)
                {
                    if (sn.Name == subnetName) { target = sn; break; }
                }
                if (target != null) break;
            }
            if (target == null)
            {
                SendJson(ctx.Response, 404, new { error = "Subnet not found: " + subnetName });
                return;
            }

            var result = new Dictionary<string, object>();
            result["subnet"] = DumpEngineeringObject(target, "Subnet", maxDepth: 1);

            // 각 Node 도 dump (Node 의 hidden composition 확인)
            var nodeDumps = new List<object>();
            try
            {
                foreach (Node n in target.Nodes)
                {
                    var d = DumpEngineeringObject(n, "Node", maxDepth: 1) as Dictionary<string, object>;
                    if (d != null)
                    {
                        try { d["NodeName"] = n.Name; } catch { }
                        try { d["NodeId"] = n.NodeId; } catch { }
                        try { d["NodeType"] = n.NodeType.ToString(); } catch { }
                    }
                    nodeDumps.Add(d);
                }
            }
            catch (Exception ex) { nodeDumps.Add(new { error = ex.Message }); }
            result["nodes"] = nodeDumps;

            // IoSystems 도 dump
            var ioSystemDumps = new List<object>();
            try
            {
                foreach (var ios in target.IoSystems)
                {
                    ioSystemDumps.Add(DumpEngineeringObject(ios, "IoSystem", maxDepth: 1));
                }
            }
            catch (Exception ex) { ioSystemDumps.Add(new { error = ex.Message }); }
            result["ioSystems"] = ioSystemDumps;

            // Subnet GetService 시도
            var services = new List<string>();
            try
            {
                var sip = target as IEngineeringServiceProvider;
                if (sip != null)
                {
                    foreach (var info in sip.GetServiceInfos())
                    {
                        string svcType = null;
                        try { svcType = info.GetType().GetProperty("Type")?.GetValue(info)?.ToString(); } catch { }
                        services.Add(svcType ?? info.GetType().FullName);
                    }
                }
            }
            catch { }
            result["services"] = services;

            SendJson(ctx.Response, 200, result);
        }

        static DeviceItem FindDeviceItemNamedRecursive(DeviceItem di, string name)
        {
            if (di.Name == name) return di;
            foreach (DeviceItem child in di.DeviceItems)
            {
                var found = FindDeviceItemNamedRecursive(child, name);
                if (found != null) return found;
            }
            return null;
        }

        static void CollectDeviceItemNames(DeviceItem di, List<string> result)
        {
            result.Add(di.Name);
            foreach (DeviceItem child in di.DeviceItems)
            {
                CollectDeviceItemNames(child, result);
            }
        }

        static DeviceItem FindCpuRecursive(DeviceItem di)
        {
            try
            {
                var sc = ((IEngineeringServiceProvider)di).GetService<SoftwareContainer>();
                if (sc != null && sc.Software is PlcSoftware) return di;
            }
            catch { }
            foreach (DeviceItem child in di.DeviceItems)
            {
                var found = FindCpuRecursive(child);
                if (found != null) return found;
            }
            return null;
        }

        static void DumpDeviceItemsRecursive(DeviceItemComposition items, string filter, List<object> result, int depth)
        {
            if (depth > 5) return;
            foreach (DeviceItem di in items)
            {
                bool include = string.IsNullOrEmpty(filter) || di.Name == filter;
                if (include)
                {
                    var d = new Dictionary<string, object>();
                    d["name"] = di.Name;
                    d["type"] = di.TypeIdentifier;
                    d["depth"] = depth;
                    d["dump"] = DumpEngineeringObject(di, "DeviceItem", maxDepth: 1);

                    // NetworkInterface service
                    try
                    {
                        var netIf = ((IEngineeringServiceProvider)di).GetService<NetworkInterface>();
                        if (netIf != null)
                        {
                            d["networkInterface"] = DumpEngineeringObject(netIf, "NetworkInterface", maxDepth: 2);
                        }
                    }
                    catch { }
                    result.Add(d);
                }
                DumpDeviceItemsRecursive(di.DeviceItems, filter, result, depth + 1);
            }
        }

        // 객체의 properties + GetCompositionInfos + GetInvocationInfos + GetAttributeInfos dump
        static object DumpEngineeringObject(object obj, string label, int maxDepth)
        {
            if (obj == null) return null;
            var d = new Dictionary<string, object>();
            d["__type"] = obj.GetType().FullName;
            d["__label"] = label;

            // 1) typed properties
            var props = new List<string>();
            foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (p.Name == "Parent") continue;
                props.Add(p.Name + ":" + p.PropertyType.Name);
            }
            d["typedProperties"] = props;

            // IEngineeringObject 호출 (직접 또는 reflection)
            var ieo = obj as IEngineeringObject;
            if (ieo != null)
            {
                // GetCompositionInfos
                var compInfos = new List<object>();
                try
                {
                    foreach (var info in ieo.GetCompositionInfos())
                    {
                        string n = null;
                        try { n = info.GetType().GetProperty("Name")?.GetValue(info) as string; } catch { }
                        if (string.IsNullOrEmpty(n)) continue;

                        int count = -1; var firstNames = new List<string>(); string compType = null;
                        try
                        {
                            var compObj = ieo.GetComposition(n);
                            if (compObj != null)
                            {
                                compType = compObj.GetType().FullName;
                                var countProp = compObj.GetType().GetProperty("Count");
                                if (countProp != null) try { count = (int)countProp.GetValue(compObj); } catch { }
                                if (compObj is System.Collections.IEnumerable enu)
                                {
                                    int c = 0;
                                    foreach (var item in enu)
                                    {
                                        c++;
                                        if (firstNames.Count < 10)
                                        {
                                            string itemName = null;
                                            try { itemName = item?.GetType().GetProperty("Name")?.GetValue(item) as string; } catch { }
                                            firstNames.Add(itemName ?? item?.GetType().Name ?? "<null>");
                                        }
                                    }
                                    if (count < 0) count = c;
                                }
                            }
                        }
                        catch (Exception ex) { compInfos.Add(new { name = n, error = ex.Message }); continue; }
                        compInfos.Add(new { name = n, type = compType, count = count, items = firstNames });
                    }
                }
                catch (Exception ex) { compInfos.Add(new { error = ex.Message }); }
                d["compositions"] = compInfos;

                // GetInvocationInfos
                var invInfos = new List<object>();
                try
                {
                    foreach (var info in ieo.GetInvocationInfos())
                    {
                        string n = null;
                        try { n = info.GetType().GetProperty("Name")?.GetValue(info) as string; } catch { }
                        invInfos.Add(n);
                    }
                }
                catch { }
                d["invocations"] = invInfos;
            }

            return d;
        }

        static List<object> CollectDeviceItems(DeviceItemComposition items, int depth)
        {
            var result = new List<object>();
            foreach (DeviceItem di in items)
            {
                var children = CollectDeviceItems(di.DeviceItems, depth + 1);
                result.Add(new {
                    name = di.Name,
                    type = di.TypeIdentifier,
                    classification = di.Classification.ToString(),
                    position = di.PositionNumber,
                    depth,
                    childCount = children.Count,
                    children
                });
            }
            return result;
        }

        // 모든 DeviceItem 트리 재귀 순회 — top-level 은 항상, 깊은 child 는 software 있을 때만 추가
        // Unified HMI 의 HmiSoftware 는 깊은 child 에 있을 수 있어 재귀 필수.
        static void CollectDeviceItemsWithSoftware(DeviceItemComposition items, List<object> result, int depth = 0)
        {
            foreach (DeviceItem di in items)
            {
                string kind = "unknown";
                bool hasSoftware = false;
                try
                {
                    var sc = ((IEngineeringServiceProvider)di).GetService<SoftwareContainer>();
                    if (sc != null && sc.Software != null)
                    {
                        hasSoftware = true;
                        if (sc.Software is PlcSoftware) kind = "PLC";
                        else if (sc.Software is HmiTarget) kind = "HMI";
                        else if (sc.Software is HmiSoftware) kind = "HMI_Unified";
                        else kind = sc.Software.GetType().Name;
                    }
                }
                catch { }

                // top-level 은 항상 추가, 깊은 child 는 software 있을 때만
                if (depth == 0 || hasSoftware)
                {
                    result.Add(new
                    {
                        name = di.Name,
                        type = di.TypeIdentifier,
                        softwareKind = kind,
                        hasSoftware = hasSoftware
                    });
                }

                // 재귀
                CollectDeviceItemsWithSoftware(di.DeviceItems, result, depth + 1);
            }
        }

        static void GetDeviceItemAttributes(HttpListenerContext ctx)
        {
            string deviceName = ctx.Request.QueryString["device"];
            string itemPath = ctx.Request.QueryString["item"];
            string projectName = ctx.Request.QueryString["project"];
            EnsureConnected();

            if (string.IsNullOrEmpty(deviceName) || string.IsNullOrEmpty(itemPath))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?device= and ?item= parameters. item is slash-separated path e.g. 'S210/PROFINET interface'" });
                return;
            }

            var di = FindDeviceItemByPath(deviceName, itemPath, projectName);
            if (di == null) { SendJson(ctx.Response, 404, new { error = "DeviceItem not found: " + itemPath }); return; }

            var attributes = ReadAllAttributes(di);
            var extraSections = BuildExtraSections(di);
            SendJson(ctx.Response, 200, new {
                device = deviceName,
                item = itemPath,
                attributeCount = attributes.Count,
                attributes,
                extraSections
            });
        }

        // PROFINET 인터페이스 DeviceItem 의 경우, NetworkInterface 의 IoConnectors / IoControllers 같은
        // 서브 IEngineeringObject 의 attribute 도 노출. Watchdog time / Update time / Accepted update cycles 등.
        static List<object> BuildExtraSections(DeviceItem di)
        {
            var sections = new List<object>();
            NetworkInterface netIf = null;
            try { netIf = ((IEngineeringServiceProvider)di).GetService<NetworkInterface>(); } catch { }

            // NetworkInterface 가 있을 때만 IoConnectors / IoControllers 노출
            if (netIf != null)
            {
                // IoConnectors: 슬레이브 측 — Watchdog time, Update time, AcceptedUpdateCyclesWithoutIoData 등
                try
                {
                    int idx = 0;
                    foreach (IoConnector ioc in netIf.IoConnectors)
                    {
                        sections.Add(new {
                            kind = "IoConnector",
                            index = idx,
                            target = "IoConnector[" + idx + "]",
                            title = "🔌 PROFINET IO Connector #" + idx + " (Update time · Watchdog · Sync 등)",
                            attributes = ReadAllAttributesOf((IEngineeringObject)ioc)
                        });
                        idx++;
                    }
                }
                catch { }

                // IoControllers: 마스터 측 — SendClock 등
                try
                {
                    int idx = 0;
                    foreach (var ioc in netIf.IoControllers)
                    {
                        sections.Add(new {
                            kind = "IoController",
                            index = idx,
                            target = "IoController[" + idx + "]",
                            title = "🎛 PROFINET IO Controller #" + idx + " (Send clock 등)",
                            attributes = ReadAllAttributesOf((IEngineeringObject)ioc)
                        });
                        idx++;
                    }
                }
                catch { }
            }

            // DeviceItem.GetAttribute("Items") → DeviceItemAssociation 의 각 DeviceItem 도 별도 섹션으로
            //    (HF 모듈의 채널/submodule 이 여기 있는 경우 — DeviceItems composition 과는 다른 별도 컬렉션)
            try
            {
                var itemsObj = ((IEngineeringObject)di).GetAttribute("Items");
                if (itemsObj is System.Collections.IEnumerable assoc)
                {
                    int idx = 0;
                    foreach (var item in assoc)
                    {
                        if (item is DeviceItem subDi)
                        {
                            var subAttrs = ReadAllAttributesOf((IEngineeringObject)subDi);
                            if (subAttrs.Count > 0)
                            {
                                sections.Add(new
                                {
                                    kind = "Items",
                                    index = idx,
                                    target = "Items[" + idx + "]",
                                    title = "📁 Items[" + idx + "] · " + (subDi.Name ?? "?") + " (" + subAttrs.Count + " attrs)",
                                    attributes = subAttrs
                                });
                            }
                        }
                        idx++;
                    }
                }
            }
            catch { }

            // MRP 인스턴스/도메인 — indexer 패턴으로 안전 접근 확인됨 (foreach 가 SCALANCE 에서 크래시 원인이었음).
            // 각 인스턴스/도메인이 별도 ⚙ 섹션으로 노출되어 인스턴스별 편집 가능.
            BuildFeatureSection_MrpInstances(di, sections);
            BuildFeatureSection_MrpDomains(di, sections);

            // 일반 hidden composition (HF 모듈 채널 등) — GetCompositionInfos 로 자동 탐색
            try
            {
                var ieo = (IEngineeringObject)di;
                var compInfos = ieo.GetCompositionInfos();
                foreach (var compInfo in compInfos)
                {
                    string compName = null;
                    try { compName = compInfo.GetType().GetProperty("Name")?.GetValue(compInfo, null) as string; } catch { }
                    if (string.IsNullOrEmpty(compName)) continue;
                    if (compName == "DeviceItems") continue; // 이미 좌측 트리에서 다룸

                    object comp = null;
                    try { comp = ieo.GetComposition(compName); } catch { }
                    if (comp == null) continue;

                    var enumComp = comp as System.Collections.IEnumerable;
                    if (enumComp == null) continue;

                    int idx = 0;
                    foreach (var item in enumComp)
                    {
                        if (!(item is IEngineeringObject subIeo)) { idx++; continue; }
                        string itemName = null;
                        try { itemName = subIeo.GetAttribute("Name") as string; } catch { }
                        if (string.IsNullOrEmpty(itemName)) itemName = compName + "[" + idx + "]";

                        var subAttrs = ReadAllAttributesOf(subIeo);
                        if (subAttrs.Count > 0)
                        {
                            sections.Add(new {
                                kind = compName,
                                index = idx,
                                target = compName + "[" + idx + "]",
                                title = "📦 " + compName + "[" + idx + "] · " + itemName + " (" + subAttrs.Count + " attrs)",
                                attributes = subAttrs
                            });
                        }
                        idx++;
                    }
                }
            }
            catch { }

            return sections;
        }

        // ── MRP 데이터 별도 endpoint (opt-in) ──
        // GET /api/device/mrp?device=&item=&project=
        // SCALANCE 등에서 호출 시 TIA Portal 이 크래시할 수 있음 — 명시적 요청 시에만 호출.
        static void GetMrpData(HttpListenerContext ctx)
        {
            string deviceName = ctx.Request.QueryString["device"];
            string itemPath = ctx.Request.QueryString["item"];
            string projectName = ctx.Request.QueryString["project"];
            EnsureConnected();

            if (string.IsNullOrEmpty(deviceName) || string.IsNullOrEmpty(itemPath))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?device= and ?item= parameters" });
                return;
            }

            var di = FindDeviceItemByPath(deviceName, itemPath, projectName);
            if (di == null) { SendJson(ctx.Response, 404, new { error = "DeviceItem not found: " + itemPath }); return; }

            var sections = new List<object>();
            BuildFeatureSection_MrpInstances(di, sections);
            BuildFeatureSection_MrpDomains(di, sections);

            SendJson(ctx.Response, 200, new {
                device = deviceName,
                item = itemPath,
                warning = "이 endpoint 는 일부 디바이스(SCALANCE 등)에서 TIA Portal 강제 종료를 일으킬 수 있습니다.",
                extraSections = sections
            });
        }

        // ── 모든 MRP instance 의 Role 일괄 변경 (opt-in, 크래시 위험) ──
        // PUT /api/device/mrp/set-all-roles  body: {project?, device, item, role, attributeName?(default "MediaRedundancyRole")}
        // role 값: "NotInRing" / "Manager" / "Client" / "ManagerAuto"  (Siemens.Engineering.HW.MediaRedundancyRole)
        // 안전 모드: foreach 미사용, indexer 와 .Count 만 사용, per-instance try/catch
        static void SetAllMrpRoles(HttpListenerContext ctx)
        {
            EnsureConnected();
            var body = ReadJsonBody(ctx);
            if (body == null) { SendJson(ctx.Response, 400, new { error = "Empty body" }); return; }

            string projectName = body.ContainsKey("project") ? body["project"] as string : null;
            string deviceName = body.ContainsKey("device") ? body["device"] as string : null;
            string itemPath = body.ContainsKey("item") ? body["item"] as string : null;
            string role = body.ContainsKey("role") ? body["role"] as string : null;
            string attrName = body.ContainsKey("attributeName") ? body["attributeName"] as string : null;
            if (string.IsNullOrEmpty(attrName)) attrName = "MediaRedundancyRole";

            if (string.IsNullOrEmpty(deviceName) || string.IsNullOrEmpty(itemPath) || string.IsNullOrEmpty(role))
            {
                SendJson(ctx.Response, 400, new { error = "필요 필드: device, item, role" });
                return;
            }

            var di = FindDeviceItemByPath(deviceName, itemPath, projectName);
            if (di == null) { SendJson(ctx.Response, 404, new { error = "DeviceItem not found: " + itemPath }); return; }

            object owner = null;
            try { owner = ((IEngineeringServiceProvider)di).GetService<Siemens.Engineering.HW.Features.MrpInstancesOwner>(); }
            catch (Exception ex) { SendJson(ctx.Response, 500, new { error = "MrpInstancesOwner GetService 실패: " + ex.Message }); return; }
            if (owner == null) { SendJson(ctx.Response, 404, new { error = "MrpInstancesOwner not available" }); return; }

            // .MrpInstances 컴포지션 (reflection 으로 안전하게)
            object instances = null;
            try { instances = owner.GetType().GetProperty("MrpInstances")?.GetValue(owner, null); } catch { }
            if (instances == null) { SendJson(ctx.Response, 404, new { error = "MrpInstances composition null" }); return; }

            int count = 0;
            try
            {
                var cp = instances.GetType().GetProperty("Count");
                if (cp != null) count = (int)cp.GetValue(instances, null);
            }
            catch (Exception ex) { SendJson(ctx.Response, 500, new { error = "MrpInstances.Count 접근 실패: " + ex.Message }); return; }

            if (count == 0) { SendJson(ctx.Response, 200, new { count, results = new object[0] }); return; }

            // role 값을 enum 으로 coerce
            object roleValue = null;
            try { roleValue = Enum.Parse(typeof(Siemens.Engineering.HW.MediaRedundancyRole), role, true); }
            catch (Exception ex) { SendJson(ctx.Response, 400, new { error = "role 값 invalid: '" + role + "'. 가능한 값: NotInRing, Manager, Client, ManagerAuto. (" + ex.Message + ")" }); return; }

            var results = new List<object>();
            int okCount = 0, errCount = 0;

            // indexer 로 한 개씩 접근 (foreach 가 SCALANCE 에서 크래시 일으킬 가능성 회피)
            var indexer = instances.GetType().GetProperty("Item", new[] { typeof(int) });
            for (int i = 0; i < count; i++)
            {
                object mi = null;
                try { mi = indexer?.GetValue(instances, new object[] { i }); }
                catch (Exception ex) { errCount++; results.Add(new { index = i, status = "error", phase = "indexer", error = ex.Message }); continue; }
                if (mi == null) { errCount++; results.Add(new { index = i, status = "error", phase = "null instance" }); continue; }

                var ieo = mi as IEngineeringObject;
                if (ieo == null) { errCount++; results.Add(new { index = i, status = "error", phase = "not IEngineeringObject" }); continue; }

                string instanceName = null;
                try { instanceName = ieo.GetAttribute("Name") as string; } catch { }

                object oldVal = null;
                try { oldVal = ieo.GetAttribute(attrName); } catch { }

                try
                {
                    ieo.SetAttribute(attrName, roleValue);
                    object newVal = null;
                    try { newVal = ieo.GetAttribute(attrName); } catch { }
                    okCount++;
                    results.Add(new {
                        index = i,
                        instanceName,
                        status = "ok",
                        oldValue = oldVal?.ToString(),
                        newValue = newVal?.ToString()
                    });
                }
                catch (Exception ex)
                {
                    errCount++;
                    results.Add(new {
                        index = i,
                        instanceName,
                        status = "error",
                        phase = "SetAttribute",
                        error = ex.Message,
                        innerError = ex.InnerException?.Message
                    });
                }
            }

            SendJson(ctx.Response, 200, new {
                device = deviceName,
                item = itemPath,
                attributeName = attrName,
                requestedRole = role,
                count,
                okCount,
                errCount,
                results
            });
        }

        // Search helpers (안전 서비스만)
        static void SearchInFeature_MrpInstances(DeviceItem di, string path, System.Text.RegularExpressions.Regex regex,
            List<object> matches, ref int scannedItems, ref int scannedAttrs)
        {
            try
            {
                var owner = ((IEngineeringServiceProvider)di).GetService<Siemens.Engineering.HW.Features.MrpInstancesOwner>();
                if (owner == null) return;
                int idx = 0;
                foreach (var mi in owner.MrpInstances)
                {
                    scannedItems++;
                    string itemName = null;
                    try { itemName = mi.Name; } catch { }
                    try
                    {
                        foreach (var info in ((IEngineeringObject)mi).GetAttributeInfos())
                        {
                            scannedAttrs++;
                            if (!regex.IsMatch(info.Name)) continue;
                            string val = null;
                            try { var v = ((IEngineeringObject)mi).GetAttribute(info.Name); val = v != null ? v.ToString() : null; } catch { }
                            matches.Add(new {
                                path = path + " > Feature:MrpInstancesOwner.MrpInstances[" + idx + "] (" + (itemName ?? "?") + ")",
                                attribute = info.Name,
                                value = val,
                                target = "Feature:MrpInstancesOwner.MrpInstances[" + idx + "]",
                                location = "Feature.Composition"
                            });
                        }
                    }
                    catch { }
                    idx++;
                }
            }
            catch { }
        }

        static void SearchInFeature_MrpDomains(DeviceItem di, string path, System.Text.RegularExpressions.Regex regex,
            List<object> matches, ref int scannedItems, ref int scannedAttrs)
        {
            try
            {
                var owner = ((IEngineeringServiceProvider)di).GetService<Siemens.Engineering.HW.Features.MrpDomainOwner>();
                if (owner == null) return;
                int idx = 0;
                foreach (var dom in owner.MrpDomains)
                {
                    scannedItems++;
                    string itemName = null;
                    try { itemName = dom.Name; } catch { }
                    try
                    {
                        foreach (var info in ((IEngineeringObject)dom).GetAttributeInfos())
                        {
                            scannedAttrs++;
                            if (!regex.IsMatch(info.Name)) continue;
                            string val = null;
                            try { var v = ((IEngineeringObject)dom).GetAttribute(info.Name); val = v != null ? v.ToString() : null; } catch { }
                            matches.Add(new {
                                path = path + " > Feature:MrpDomainOwner.MrpDomains[" + idx + "] (" + (itemName ?? "?") + ")",
                                attribute = info.Name,
                                value = val,
                                target = "Feature:MrpDomainOwner.MrpDomains[" + idx + "]",
                                location = "Feature.Composition"
                            });
                        }
                    }
                    catch { }
                    idx++;
                }
            }
            catch { }
        }

        // MrpInstancesOwner.MrpInstances 각 인스턴스를 섹션으로 (indexer 만 사용, foreach 미사용 - SCALANCE 안전)
        static void BuildFeatureSection_MrpInstances(DeviceItem di, List<object> sections)
        {
            try
            {
                var owner = ((IEngineeringServiceProvider)di).GetService<Siemens.Engineering.HW.Features.MrpInstancesOwner>();
                if (owner == null) return;

                object instances = null;
                try { instances = owner.GetType().GetProperty("MrpInstances")?.GetValue(owner, null); } catch { }
                if (instances == null) return;
                int count = 0;
                try { count = (int)(instances.GetType().GetProperty("Count")?.GetValue(instances, null) ?? 0); } catch { return; }

                var indexer = instances.GetType().GetProperty("Item", new[] { typeof(int) });
                for (int i = 0; i < count; i++)
                {
                    object mi = null;
                    try { mi = indexer?.GetValue(instances, new object[] { i }); } catch { continue; }
                    if (!(mi is IEngineeringObject ieo)) continue;

                    string name = null;
                    try { name = ieo.GetAttribute("Name") as string; } catch { }
                    if (string.IsNullOrEmpty(name)) name = "MrpInstance " + (i + 1);

                    var attrs = ReadAllAttributesOf(ieo);
                    sections.Add(new
                    {
                        kind = "Feature:MrpInstancesOwner.MrpInstances",
                        index = i,
                        target = "Feature:MrpInstancesOwner.MrpInstances[" + i + "]",
                        title = "⚙ MRP Instance " + (i + 1) + " · " + name + " (" + attrs.Count + " attrs)",
                        attributes = attrs
                    });
                }
            }
            catch { }
        }

        // MrpDomainOwner.MrpDomains 각 도메인을 섹션으로 (indexer 만 사용)
        static void BuildFeatureSection_MrpDomains(DeviceItem di, List<object> sections)
        {
            try
            {
                var owner = ((IEngineeringServiceProvider)di).GetService<Siemens.Engineering.HW.Features.MrpDomainOwner>();
                if (owner == null) return;

                object domains = null;
                try { domains = owner.GetType().GetProperty("MrpDomains")?.GetValue(owner, null); } catch { }
                if (domains == null) return;
                int count = 0;
                try { count = (int)(domains.GetType().GetProperty("Count")?.GetValue(domains, null) ?? 0); } catch { return; }

                var indexer = domains.GetType().GetProperty("Item", new[] { typeof(int) });
                for (int i = 0; i < count; i++)
                {
                    object dom = null;
                    try { dom = indexer?.GetValue(domains, new object[] { i }); } catch { continue; }
                    if (!(dom is IEngineeringObject ieo)) continue;

                    string name = null;
                    try { name = ieo.GetAttribute("Name") as string; } catch { }
                    if (string.IsNullOrEmpty(name)) name = "MrpDomain " + (i + 1);

                    var attrs = ReadAllAttributesOf(ieo);
                    sections.Add(new
                    {
                        kind = "Feature:MrpDomainOwner.MrpDomains",
                        index = i,
                        target = "Feature:MrpDomainOwner.MrpDomains[" + i + "]",
                        title = "⚙ MRP Domain · " + name + " (" + attrs.Count + " attrs)",
                        attributes = attrs
                    });
                }
            }
            catch { }
        }

        // GetServiceInfos 로 svcType.Name == typeName 인 서비스 찾아서 GetService<T> 반환
        static object ResolveServiceByName(DeviceItem di, string typeName)
        {
            var sip = di as IEngineeringServiceProvider;
            if (sip == null) return null;
            System.Collections.IEnumerable serviceInfos = null;
            try { serviceInfos = sip.GetServiceInfos() as System.Collections.IEnumerable; } catch { }
            if (serviceInfos == null) return null;

            var gsMethod = typeof(IEngineeringServiceProvider).GetMethod("GetService");
            foreach (var sInfo in serviceInfos)
            {
                Type svcType = null;
                try { svcType = sInfo.GetType().GetProperty("Type")?.GetValue(sInfo, null) as Type; } catch { }
                if (svcType == null || svcType.Name != typeName) continue;
                try
                {
                    var generic = gsMethod.MakeGenericMethod(svcType);
                    return generic.Invoke(sip, null);
                }
                catch { return null; }
            }
            return null;
        }

        // target 문자열 ("IoConnector[0]" / "Submodules[2]" / "Feature:MrpInstancesOwner.MrpInstances[0]" 등) → 실제 IEngineeringObject
        static IEngineeringObject ResolveSubTarget(DeviceItem di, string target)
        {
            if (string.IsNullOrEmpty(target)) return (IEngineeringObject)di;

            // "Feature:<TypeName>" — 서비스 자체 (composition 없이)
            var mf = System.Text.RegularExpressions.Regex.Match(target, @"^Feature:([A-Za-z_]\w*)$");
            if (mf.Success)
            {
                string typeName = mf.Groups[1].Value;
                return ResolveServiceByName(di, typeName) as IEngineeringObject;
            }

            // "Feature:<TypeName>.<Composition>[N]"
            var mfc = System.Text.RegularExpressions.Regex.Match(target, @"^Feature:([A-Za-z_]\w*)\.([A-Za-z_]\w*)\[(\d+)\]$");
            if (mfc.Success)
            {
                string typeName = mfc.Groups[1].Value;
                string compName = mfc.Groups[2].Value;
                int fidx = int.Parse(mfc.Groups[3].Value);
                var svc = ResolveServiceByName(di, typeName) as IEngineeringObject;
                if (svc == null) return (IEngineeringObject)di;
                try
                {
                    var comp = svc.GetComposition(compName);
                    if (comp is System.Collections.IEnumerable ie)
                    {
                        int i = 0;
                        foreach (var item in ie)
                        {
                            if (i == fidx) return item as IEngineeringObject;
                            i++;
                        }
                    }
                }
                catch { }
                return (IEngineeringObject)di;
            }

            var m = System.Text.RegularExpressions.Regex.Match(target, @"^([A-Za-z_]\w*)\[(\d+)\]$");
            if (!m.Success) return (IEngineeringObject)di;
            string kind = m.Groups[1].Value;
            int idx = int.Parse(m.Groups[2].Value);

            // 1) NetworkInterface 의 IoConnectors / IoControllers 우선
            try
            {
                var netIf = ((IEngineeringServiceProvider)di).GetService<NetworkInterface>();
                if (netIf != null)
                {
                    if (kind == "IoConnector")
                    {
                        int i = 0;
                        foreach (IoConnector ioc in netIf.IoConnectors)
                        {
                            if (i == idx) return (IEngineeringObject)ioc;
                            i++;
                        }
                    }
                    else if (kind == "IoController")
                    {
                        int i = 0;
                        foreach (var ioc in netIf.IoControllers)
                        {
                            if (i == idx) return (IEngineeringObject)ioc;
                            i++;
                        }
                    }
                }
            }
            catch { }

            // 2) Generic: GetComposition(kind) 로 직접 접근
            try
            {
                var ieo = (IEngineeringObject)di;
                var comp = ieo.GetComposition(kind);
                if (comp is System.Collections.IEnumerable ie)
                {
                    int i = 0;
                    foreach (var item in ie)
                    {
                        if (i == idx) return item as IEngineeringObject;
                        i++;
                    }
                }
            }
            catch { }

            // 3) GetAttribute(kind) 로 DeviceItemAssociation 같은 collection attribute 처리
            try
            {
                var attrObj = ((IEngineeringObject)di).GetAttribute(kind);
                if (attrObj is System.Collections.IEnumerable ie2)
                {
                    int i = 0;
                    foreach (var item in ie2)
                    {
                        if (i == idx) return item as IEngineeringObject;
                        i++;
                    }
                }
            }
            catch { }

            return (IEngineeringObject)di;
        }

        static void SetDeviceItemAttributes(HttpListenerContext ctx)
        {
            EnsureConnected();

            byte[] body;
            using (var ms = new MemoryStream())
            {
                ctx.Request.InputStream.CopyTo(ms);
                body = ms.ToArray();
            }
            string bodyStr = Encoding.UTF8.GetString(body);

            Dictionary<string, object> parsed;
            try { parsed = json.Deserialize<Dictionary<string, object>>(bodyStr); }
            catch (Exception ex) { SendJson(ctx.Response, 400, new { error = "JSON 파싱 실패: " + ex.Message }); return; }

            if (parsed == null) { SendJson(ctx.Response, 400, new { error = "Empty body" }); return; }

            string deviceName = parsed.ContainsKey("device") ? parsed["device"] as string : null;
            string itemPath = parsed.ContainsKey("item") ? parsed["item"] as string : null;
            string projectName = parsed.ContainsKey("project") ? parsed["project"] as string : null;

            if (string.IsNullOrEmpty(deviceName) || string.IsNullOrEmpty(itemPath))
            {
                SendJson(ctx.Response, 400, new { error = "body에 device, item 필드가 필요합니다. 예: {\"device\":\"PLC_1\",\"item\":\"PLC_1\",\"attributes\":[{\"name\":\"IpAddress\",\"value\":\"192.168.0.1\"}]}" });
                return;
            }

            object attrsRaw = parsed.ContainsKey("attributes") ? parsed["attributes"] : null;
            var attrList = new List<object>();
            if (attrsRaw is object[] arr)
            {
                attrList.AddRange(arr);
            }
            else if (attrsRaw is System.Collections.IEnumerable ie && !(attrsRaw is string))
            {
                foreach (var x in ie) attrList.Add(x);
            }
            if (attrList.Count == 0)
            {
                SendJson(ctx.Response, 400, new {
                    error = "attributes 배열이 비어있거나 올바르지 않습니다",
                    receivedType = attrsRaw != null ? attrsRaw.GetType().FullName : "null",
                    bodyPreview = bodyStr.Length > 500 ? bodyStr.Substring(0, 500) : bodyStr
                });
                return;
            }

            var di = FindDeviceItemByPath(deviceName, itemPath, projectName);
            if (di == null) { SendJson(ctx.Response, 404, new { error = "DeviceItem not found: " + itemPath }); return; }

            var engObj = (IEngineeringObject)di;

            // AttributeInfo 인덱싱 (타입 조회용)
            var infoByName = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var info in engObj.GetAttributeInfos())
                {
                    infoByName[info.Name] = info;
                }
            }
            catch { }

            int okCount = 0, errCount = 0;
            var results = new List<object>();
            foreach (var entry in attrList)
            {
                var d = entry as Dictionary<string, object>;
                if (d == null) { errCount++; results.Add(new { status = "error", error = "attributes 요소가 객체가 아님" }); continue; }

                string attrName = d.ContainsKey("name") ? d["name"] as string : null;
                if (string.IsNullOrEmpty(attrName)) { errCount++; results.Add(new { status = "error", error = "name 누락" }); continue; }

                object rawValue = d.ContainsKey("value") ? d["value"] : null;
                string targetSpec = d.ContainsKey("target") ? d["target"] as string : null;

                // 서브 객체 타겟 (IoConnector[N], IoController[N]) 처리 — DeviceItem 폴백 로직 없이 단순 SetAttribute
                if (!string.IsNullOrEmpty(targetSpec))
                {
                    IEngineeringObject subObj = ResolveSubTarget(di, targetSpec);
                    if (subObj == null || ReferenceEquals(subObj, di))
                    {
                        errCount++;
                        results.Add(new { name = attrName, target = targetSpec, status = "error", error = "서브 객체를 찾을 수 없음: " + targetSpec });
                        continue;
                    }

                    object subCur = null;
                    try { subCur = subObj.GetAttribute(attrName); } catch { }
                    Type subType = subCur != null ? subCur.GetType() : null;
                    if (subType == null)
                    {
                        // AttributeInfos 에서 타입 찾기
                        try
                        {
                            foreach (var info in subObj.GetAttributeInfos())
                            {
                                if (string.Equals(info.Name, attrName, StringComparison.OrdinalIgnoreCase))
                                {
                                    var gm = info.GetType().GetMethod("GetAttributeType");
                                    if (gm != null) subType = gm.Invoke(info, null) as Type;
                                    break;
                                }
                            }
                        }
                        catch { }
                    }

                    try
                    {
                        object subCoerced = CoerceValue(rawValue, subType);
                        subObj.SetAttribute(attrName, subCoerced);
                        object subNew = null;
                        try { subNew = subObj.GetAttribute(attrName); } catch { }
                        okCount++;
                        results.Add(new
                        {
                            name = attrName,
                            target = targetSpec,
                            status = "ok",
                            method = "SetAttribute(" + targetSpec + ")",
                            targetType = subType != null ? subType.FullName : null,
                            oldValue = subCur != null ? subCur.ToString() : null,
                            newValue = subNew != null ? subNew.ToString() : null
                        });
                    }
                    catch (Exception subEx)
                    {
                        errCount++;
                        results.Add(new
                        {
                            name = attrName,
                            target = targetSpec,
                            status = "error",
                            error = subEx.Message,
                            innerError = subEx.InnerException != null ? subEx.InnerException.Message : null,
                            targetType = subType != null ? subType.FullName : null
                        });
                    }
                    continue;
                }

                object currentValue = null;
                try { currentValue = engObj.GetAttribute(attrName); } catch { }

                Type targetType = null;
                if (currentValue != null) targetType = currentValue.GetType();
                if (targetType == null && infoByName.ContainsKey(attrName))
                {
                    try
                    {
                        var info = infoByName[attrName];
                        var m = info.GetType().GetMethod("GetAttributeType");
                        if (m != null) targetType = m.Invoke(info, null) as Type;
                    }
                    catch { }
                }

                try
                {
                    object coerced = CoerceValue(rawValue, targetType);
                    string usedMethod = "SetAttribute";
                    string fallbackError = null;

                    try { engObj.SetAttribute(attrName, coerced); }
                    catch (Exception setEx) { fallbackError = setEx.Message; }

                    object newVal = null;
                    try { newVal = engObj.GetAttribute(attrName); } catch { }

                    string oldStr = currentValue != null ? currentValue.ToString() : null;
                    string newStr = newVal != null ? newVal.ToString() : null;
                    string expectedStr = coerced != null ? coerced.ToString() : null;

                    bool matchesExpected = string.Equals(newStr, expectedStr, StringComparison.Ordinal);

                    // 폴백 1: SetAttribute가 무시된 경우 DeviceItem의 .NET 프로퍼티 setter 시도
                    if (!matchesExpected)
                    {
                        try
                        {
                            var prop = di.GetType().GetProperty(attrName,
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.IgnoreCase);
                            if (prop != null && prop.CanWrite)
                            {
                                object propCoerced = CoerceValue(rawValue, prop.PropertyType);
                                prop.SetValue(di, propCoerced, null);
                                usedMethod = "PropertySetter";

                                try { newVal = engObj.GetAttribute(attrName); } catch { newVal = prop.GetValue(di, null); }
                                newStr = newVal != null ? newVal.ToString() : null;
                                matchesExpected = string.Equals(newStr, expectedStr, StringComparison.Ordinal);
                            }
                        }
                        catch (Exception propEx)
                        {
                            if (fallbackError == null) fallbackError = propEx.Message;
                            else fallbackError += " | PropertySetter: " + propEx.Message;
                        }
                    }

                    // 폴백 2: Name 속성이고 루트 DeviceItem(Device와 이름 동일)이면 부모 Device.Name 변경 시도
                    // TIA Openness에서 CPU 디바이스 이름(예: PLC_1)은 실제로 Device.Name에서 관리됨
                    if (!matchesExpected && string.Equals(attrName, "Name", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            Device parentDev = FindParentDevice(di);
                            if (parentDev != null && string.Equals(parentDev.Name, oldStr, StringComparison.Ordinal))
                            {
                                string newName = rawValue as string;
                                if (newName == null && rawValue != null) newName = rawValue.ToString();
                                if (!string.IsNullOrEmpty(newName))
                                {
                                    try { ((IEngineeringObject)parentDev).SetAttribute("Name", newName); } catch { }
                                    try
                                    {
                                        var devProp = parentDev.GetType().GetProperty("Name",
                                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                        if (devProp != null && devProp.CanWrite) devProp.SetValue(parentDev, newName, null);
                                    }
                                    catch { }

                                    try { newVal = engObj.GetAttribute(attrName); } catch { }
                                    newStr = newVal != null ? newVal.ToString() : null;
                                    matchesExpected = string.Equals(newStr, expectedStr, StringComparison.Ordinal);

                                    // DeviceItem 이름은 그대로지만 Device 이름이 바뀌면 성공으로 간주
                                    if (!matchesExpected && string.Equals(parentDev.Name, newName, StringComparison.Ordinal))
                                    {
                                        matchesExpected = true;
                                        newStr = parentDev.Name + " (Device.Name 변경됨, DeviceItem.Name은 TIA 내부 고정)";
                                        usedMethod = "Device.Name";
                                    }
                                    else if (matchesExpected)
                                    {
                                        usedMethod = "Device.Name";
                                    }
                                }
                            }
                        }
                        catch (Exception devEx)
                        {
                            if (fallbackError == null) fallbackError = "Device.Name: " + devEx.Message;
                            else fallbackError += " | Device.Name: " + devEx.Message;
                        }
                    }

                    bool actuallyChanged = !string.Equals(oldStr, newStr, StringComparison.Ordinal);

                    if (actuallyChanged || matchesExpected)
                    {
                        okCount++;
                        results.Add(new
                        {
                            name = attrName,
                            status = "ok",
                            method = usedMethod,
                            targetType = targetType != null ? targetType.FullName : null,
                            oldValue = oldStr,
                            newValue = newStr,
                            expectedValue = expectedStr
                        });
                    }
                    else
                    {
                        errCount++;
                        results.Add(new
                        {
                            name = attrName,
                            status = "not_applied",
                            method = usedMethod,
                            targetType = targetType != null ? targetType.FullName : null,
                            oldValue = oldStr,
                            newValue = newStr,
                            expectedValue = expectedStr,
                            error = fallbackError ?? "SetAttribute/PropertySetter 호출은 성공했으나 값이 반영되지 않음 (읽기전용/조건부 속성일 수 있음). 예: IP주소는 'Set IP address in the project' 옵션이 켜져 있어야 하고, 디바이스 이름은 'Generate automatically' 체크 해제 필요."
                        });
                    }
                }
                catch (Exception ex)
                {
                    errCount++;
                    results.Add(new
                    {
                        name = attrName,
                        status = "error",
                        targetType = targetType != null ? targetType.FullName : null,
                        oldValue = currentValue != null ? currentValue.ToString() : null,
                        error = ex.Message,
                        innerError = ex.InnerException != null ? ex.InnerException.Message : null
                    });
                }
            }

            // 변경 후 실제 device/item path 계산 (Name 변경 시 UI가 state를 갱신하도록)
            string currentItemPath = BuildItemPath(di);
            Device parentDevAfter = FindParentDevice(di);
            string currentDeviceName = parentDevAfter != null ? parentDevAfter.Name : deviceName;
            bool renamed = !string.Equals(currentDeviceName, deviceName, StringComparison.Ordinal)
                        || !string.Equals(currentItemPath, itemPath, StringComparison.Ordinal);

            SendJson(ctx.Response, 200, new {
                device = deviceName,
                item = itemPath,
                currentDevice = currentDeviceName,
                currentItem = currentItemPath,
                renamed,
                okCount, errCount, results
            });
        }

        // ── Device NetworkInterface IP 변경 ──
        // Subnet 미연결 상태로도 Node.Address 변경 가능 (TIA GUI 와 동일 동작 목표).
        // body: { device, interfaceIndex?, interfaceType?, address, subnetMask? }
        static void SetDeviceInterfaceIp(HttpListenerContext ctx)
        {
            EnsureConnected();

            byte[] body;
            using (var ms = new MemoryStream())
            {
                ctx.Request.InputStream.CopyTo(ms);
                body = ms.ToArray();
            }
            string bodyStr = Encoding.UTF8.GetString(body);

            Dictionary<string, object> parsed;
            try { parsed = json.Deserialize<Dictionary<string, object>>(bodyStr); }
            catch (Exception ex) { SendJson(ctx.Response, 400, new { error = "JSON 파싱 실패: " + ex.Message }); return; }
            if (parsed == null) { SendJson(ctx.Response, 400, new { error = "Empty body" }); return; }

            string deviceName = parsed.ContainsKey("device") ? parsed["device"] as string : null;
            string projectName = parsed.ContainsKey("project") ? parsed["project"] as string : null;
            string address = parsed.ContainsKey("address") ? parsed["address"] as string : null;
            string subnetMask = parsed.ContainsKey("subnetMask") ? parsed["subnetMask"] as string : null;
            string interfaceType = parsed.ContainsKey("interfaceType") ? parsed["interfaceType"] as string : null;
            int interfaceIndex = -1;
            if (parsed.ContainsKey("interfaceIndex"))
            {
                try { interfaceIndex = Convert.ToInt32(parsed["interfaceIndex"]); } catch { interfaceIndex = -1; }
            }

            if (string.IsNullOrEmpty(deviceName))
            {
                SendJson(ctx.Response, 400, new { error = "body에 device 필드 필요. 예: {\"device\":\"PLC_1\",\"address\":\"192.168.0.10\"}" });
                return;
            }
            if (string.IsNullOrEmpty(address))
            {
                SendJson(ctx.Response, 400, new { error = "body에 address 필드 필요" });
                return;
            }

            // Device 찾기 (FindDeviceItemByPath 의 device-lookup 부분과 동일 패턴)
            Device targetDevice = null;
            foreach (Project proj in connectedPortal.Projects)
            {
                if (projectName != null && proj.Name != projectName) continue;
                foreach (Device dev in EnumerateAllDevices(proj))
                {
                    if (dev.Name == deviceName) { targetDevice = dev; break; }
                }
                if (targetDevice != null) break;
            }
            if (targetDevice == null)
            {
                SendJson(ctx.Response, 404, new { error = "Device not found: " + deviceName });
                return;
            }

            // NetworkInterface 목록
            var interfaces = FindAllNetworkInterfaces(targetDevice);
            if (interfaces.Count == 0)
            {
                SendJson(ctx.Response, 404, new { error = "Device 에 NetworkInterface 가 없습니다: " + deviceName });
                return;
            }

            // Interface 선택 — 명시 index 우선, 그 다음 type 매칭, 마지막 첫번째
            NetworkInterface targetIf = null;
            DeviceItem targetIfItem = null;
            if (interfaceIndex >= 0 && interfaceIndex < interfaces.Count)
            {
                targetIf = interfaces[interfaceIndex].Item1;
                targetIfItem = interfaces[interfaceIndex].Item2;
            }
            else if (!string.IsNullOrEmpty(interfaceType))
            {
                foreach (var t in interfaces)
                {
                    if (string.Equals(t.Item1.InterfaceType.ToString(), interfaceType, StringComparison.OrdinalIgnoreCase))
                    {
                        targetIf = t.Item1; targetIfItem = t.Item2; break;
                    }
                }
            }
            if (targetIf == null)
            {
                targetIf = interfaces[0].Item1;
                targetIfItem = interfaces[0].Item2;
            }

            // Node 가져오기 (NetworkInterface 1개당 보통 Node 1개)
            Node targetNode = null;
            try { foreach (Node n in targetIf.Nodes) { targetNode = n; break; } } catch { }
            if (targetNode == null)
            {
                SendJson(ctx.Response, 404, new {
                    error = "선택된 NetworkInterface 에 Node 가 없습니다",
                    interfaceType = targetIf.InterfaceType.ToString(),
                    interfaceItem = targetIfItem != null ? targetIfItem.Name : null
                });
                return;
            }

            // Address / SubnetMask 적용 (Subnet attach 없이 Node attribute 만 변경)
            var results = new Dictionary<string, object>();
            string oldAddress = null, oldMask = null;
            try { oldAddress = targetNode.GetAttribute("Address") as string; } catch { }
            try { oldMask = targetNode.GetAttribute("SubnetMask") as string; } catch { }

            bool addressOk = false, maskOk = false;
            try { targetNode.SetAttribute("Address", address); addressOk = true; results["address"] = address; }
            catch (Exception ex) { results["addressError"] = ex.Message; }

            if (!string.IsNullOrEmpty(subnetMask))
            {
                try { targetNode.SetAttribute("SubnetMask", subnetMask); maskOk = true; results["subnetMask"] = subnetMask; }
                catch (Exception ex) { results["subnetMaskError"] = ex.Message; }
            }

            // 적용 후 재조회 (실제 반영 확인)
            string nowAddress = null, nowMask = null;
            try { nowAddress = targetNode.GetAttribute("Address") as string; } catch { }
            try { nowMask = targetNode.GetAttribute("SubnetMask") as string; } catch { }

            SendJson(ctx.Response, addressOk ? 200 : 500, new {
                device = deviceName,
                interfaceType = targetIf.InterfaceType.ToString(),
                interfaceItem = targetIfItem != null ? targetIfItem.Name : null,
                addressOk, maskOk,
                before = new { address = oldAddress, subnetMask = oldMask },
                after = new { address = nowAddress, subnetMask = nowMask },
                results
            });
        }

        // ── 프로젝트 내 모든 NetworkInterface 요약 (PROFINET/PROFIBUS/MPI 등) ──
        // GET /api/network/interfaces?project=<name>
        static void ListNetworkInterfaces(HttpListenerContext ctx)
        {
            EnsureConnected();
            string projectName = ctx.Request.QueryString["project"];

            var items = new List<object>();
            foreach (Project proj in connectedPortal.Projects)
            {
                if (!string.IsNullOrEmpty(projectName) && proj.Name != projectName) continue;

                foreach (Device dev in EnumerateAllDevices(proj))
                {
                    List<Tuple<NetworkInterface, DeviceItem>> ifs;
                    try { ifs = FindAllNetworkInterfaces(dev); }
                    catch { continue; }

                    int idx = 0;
                    foreach (var pair in ifs)
                    {
                        var netIf = pair.Item1;
                        var ifItem = pair.Item2;

                        string ifType = null;
                        try { ifType = netIf.InterfaceType.ToString(); } catch { }

                        string itemPath = null;
                        try { itemPath = BuildItemPath(ifItem); } catch { }

                        string profinetName = null;
                        try { profinetName = ifItem.GetAttribute("ProfinetDeviceName") as string; } catch { }

                        Node firstNode = null;
                        try { foreach (Node n in netIf.Nodes) { firstNode = n; break; } } catch { }

                        string nodeAddress = null, nodeMask = null, subnetName = null;
                        if (firstNode != null)
                        {
                            try { nodeAddress = firstNode.GetAttribute("Address") as string; } catch { }
                            try { nodeMask = firstNode.GetAttribute("SubnetMask") as string; } catch { }
                            // 연결된 Subnet 이름 (best-effort)
                            try
                            {
                                var sub = firstNode.GetAttribute("ConnectedSubnet");
                                if (sub != null)
                                {
                                    var nameProp = sub.GetType().GetProperty("Name");
                                    if (nameProp != null) subnetName = nameProp.GetValue(sub, null) as string;
                                }
                            }
                            catch { }
                        }

                        int portCount = 0;
                        try { portCount = FindNetworkPorts(ifItem).Count; } catch { }

                        items.Add(new
                        {
                            project = proj.Name,
                            device = dev.Name,
                            interfaceIndex = idx,
                            interfaceName = ifItem.Name,
                            interfaceItemPath = itemPath,
                            interfaceType = ifType,
                            profinetDeviceName = profinetName,
                            address = nodeAddress,
                            subnetMask = nodeMask,
                            subnet = subnetName,
                            portCount,
                            hasNode = firstNode != null
                        });
                        idx++;
                    }
                }
            }

            SendJson(ctx.Response, 200, new { count = items.Count, interfaces = items });
        }

        // ── Project 내 모든 Subnet + 각 Subnet에 연결된 Node 목록 + 미연결 인터페이스 ──
        // GET /api/network/subnets?project=<name>
        static void ListSubnetsWithNodes(HttpListenerContext ctx)
        {
            EnsureConnected();
            string projectName = ctx.Request.QueryString["project"];

            var subnetsOut = new List<Dictionary<string, object>>();
            var unattached = new List<Dictionary<string, object>>();

            foreach (Project proj in connectedPortal.Projects)
            {
                if (!string.IsNullOrEmpty(projectName) && proj.Name != projectName) continue;

                // Subnet name -> 출력용 dict (nodes 리스트 포함)
                var byName = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);

                foreach (var subnet in proj.Subnets)
                {
                    string sName = ""; string sType = "";
                    try { sName = ((IEngineeringObject)subnet).GetAttribute("Name") as string ?? ""; } catch { }
                    try { sType = ((IEngineeringObject)subnet).GetAttribute("TypeIdentifier") as string ?? ""; } catch { }
                    if (!string.IsNullOrEmpty(sType) && sType.Contains(".")) sType = sType.Substring(sType.LastIndexOf('.') + 1);

                    var entry = new Dictionary<string, object>
                    {
                        { "name", sName },
                        { "type", sType },
                        { "nodes", new List<Dictionary<string, object>>() }
                    };
                    subnetsOut.Add(entry);
                    if (!byName.ContainsKey(sName)) byName[sName] = entry;
                }

                foreach (Device dev in EnumerateAllDevices(proj))
                {
                    List<Tuple<NetworkInterface, DeviceItem>> ifs;
                    try { ifs = FindAllNetworkInterfaces(dev); } catch { continue; }

                    int idx = 0;
                    foreach (var pair in ifs)
                    {
                        var netIf = pair.Item1;
                        var ifItem = pair.Item2;

                        string ifType = ""; try { ifType = netIf.InterfaceType.ToString(); } catch { }
                        string itemPath = ""; try { itemPath = BuildItemPath(ifItem); } catch { }
                        string profinetName = null; try { profinetName = ifItem.GetAttribute("ProfinetDeviceName") as string; } catch { }

                        Node firstNode = null;
                        try { foreach (Node n in netIf.Nodes) { firstNode = n; break; } } catch { }

                        string address = "", mask = "", connSubnetName = null;
                        if (firstNode != null)
                        {
                            try { address = firstNode.GetAttribute("Address") as string ?? ""; } catch { }
                            try { mask = firstNode.GetAttribute("SubnetMask") as string ?? ""; } catch { }
                            try
                            {
                                var connProp = firstNode.GetType().GetProperty("ConnectedSubnet");
                                if (connProp != null)
                                {
                                    var connSub = connProp.GetValue(firstNode, null);
                                    if (connSub != null)
                                        connSubnetName = ((IEngineeringObject)connSub).GetAttribute("Name") as string;
                                }
                            }
                            catch { }
                        }

                        var nodeInfo = new Dictionary<string, object>
                        {
                            { "project", proj.Name },
                            { "device", dev.Name },
                            { "interfaceIndex", idx },
                            { "interfaceName", ifItem.Name },
                            { "interfaceItemPath", itemPath },
                            { "interfaceType", ifType },
                            { "profinetDeviceName", profinetName },
                            { "address", address },
                            { "subnetMask", mask },
                            { "hasNode", firstNode != null },
                            { "connectedSubnet", connSubnetName }
                        };

                        if (connSubnetName != null && byName.ContainsKey(connSubnetName))
                        {
                            var list = (List<Dictionary<string, object>>)byName[connSubnetName]["nodes"];
                            list.Add(nodeInfo);
                        }
                        else
                        {
                            unattached.Add(nodeInfo);
                        }
                        idx++;
                    }
                }
            }

            SendJson(ctx.Response, 200, new
            {
                subnetCount = subnetsOut.Count,
                unattachedCount = unattached.Count,
                subnets = subnetsOut,
                unattached
            });
        }

        // ── Subnet 이름 변경 ──
        // PUT /api/network/subnet/rename  body: {project?, oldName, newName}
        static void RenameSubnet(HttpListenerContext ctx)
        {
            EnsureConnected();
            var body = ReadJsonBody(ctx);
            if (body == null) { SendJson(ctx.Response, 400, new { error = "Empty body" }); return; }

            string projectName = body.ContainsKey("project") ? body["project"] as string : null;
            string oldName = body.ContainsKey("oldName") ? body["oldName"] as string : null;
            string newName = body.ContainsKey("newName") ? body["newName"] as string : null;

            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName))
            {
                SendJson(ctx.Response, 400, new { error = "oldName, newName 필드 필요" });
                return;
            }

            foreach (Project proj in connectedPortal.Projects)
            {
                if (!string.IsNullOrEmpty(projectName) && proj.Name != projectName) continue;
                foreach (var subnet in proj.Subnets)
                {
                    string sName = null;
                    try { sName = ((IEngineeringObject)subnet).GetAttribute("Name") as string; } catch { }
                    if (sName != oldName) continue;

                    string before = sName;
                    try { ((IEngineeringObject)subnet).SetAttribute("Name", newName); }
                    catch (Exception ex) { SendJson(ctx.Response, 500, new { error = "Rename 실패: " + ex.Message }); return; }

                    string after = null;
                    try { after = ((IEngineeringObject)subnet).GetAttribute("Name") as string; } catch { }

                    SendJson(ctx.Response, 200, new { project = proj.Name, before, after, ok = after == newName });
                    return;
                }
            }
            SendJson(ctx.Response, 404, new { error = "Subnet not found: " + oldName });
        }

        // ── 특정 Subnet 의 모든 attribute 조회 (Transmission speed, Profile, Highest address, Bus parameters 등) ──
        // GET /api/network/subnet/attributes?project=<>&name=<subnetName>
        static void GetSubnetAttributes(HttpListenerContext ctx)
        {
            EnsureConnected();
            string projectName = ctx.Request.QueryString["project"];
            string subnetName = ctx.Request.QueryString["name"];
            if (string.IsNullOrEmpty(subnetName)) { SendJson(ctx.Response, 400, new { error = "?name= 필요" }); return; }

            object found = FindSubnetByName(projectName, subnetName);
            if (found == null) { SendJson(ctx.Response, 404, new { error = "Subnet not found: " + subnetName }); return; }

            var attrs = ReadAllAttributesOf((IEngineeringObject)found);
            SendJson(ctx.Response, 200, new {
                subnet = subnetName,
                typeName = found.GetType().Name,
                attributeCount = attrs.Count,
                attributes = attrs
            });
        }

        // ── Subnet attribute 수정 ──
        // PUT /api/network/subnet/attributes  body: {project?, name, attributes: [{name, value}, ...]}
        static void SetSubnetAttributes(HttpListenerContext ctx)
        {
            EnsureConnected();
            var body = ReadJsonBody(ctx);
            if (body == null) { SendJson(ctx.Response, 400, new { error = "Empty body" }); return; }

            string projectName = body.ContainsKey("project") ? body["project"] as string : null;
            string subnetName = body.ContainsKey("name") ? body["name"] as string : null;
            object attrsRaw = body.ContainsKey("attributes") ? body["attributes"] : null;

            if (string.IsNullOrEmpty(subnetName)) { SendJson(ctx.Response, 400, new { error = "name 필드 필요" }); return; }

            var attrList = new List<object>();
            if (attrsRaw is object[] arr) attrList.AddRange(arr);
            else if (attrsRaw is System.Collections.IEnumerable ie && !(attrsRaw is string))
                foreach (var x in ie) attrList.Add(x);
            if (attrList.Count == 0) { SendJson(ctx.Response, 400, new { error = "attributes 배열 필요" }); return; }

            object found = FindSubnetByName(projectName, subnetName);
            if (found == null) { SendJson(ctx.Response, 404, new { error = "Subnet not found: " + subnetName }); return; }

            var engObj = (IEngineeringObject)found;

            var infoByName = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var info in engObj.GetAttributeInfos()) infoByName[info.Name] = info; }
            catch { }

            int okCount = 0, errCount = 0;
            var results = new List<object>();
            foreach (var entry in attrList)
            {
                var d = entry as Dictionary<string, object>;
                if (d == null) { errCount++; results.Add(new { status = "error", error = "attribute 항목이 객체가 아님" }); continue; }
                string attrName = d.ContainsKey("name") ? d["name"] as string : null;
                if (string.IsNullOrEmpty(attrName)) { errCount++; results.Add(new { status = "error", error = "name 누락" }); continue; }

                object rawValue = d.ContainsKey("value") ? d["value"] : null;

                object currentValue = null;
                try { currentValue = engObj.GetAttribute(attrName); } catch { }

                Type targetType = null;
                if (currentValue != null) targetType = currentValue.GetType();
                if (targetType == null && infoByName.ContainsKey(attrName))
                {
                    try
                    {
                        var info = infoByName[attrName];
                        var m = info.GetType().GetMethod("GetAttributeType");
                        if (m != null) targetType = m.Invoke(info, null) as Type;
                    }
                    catch { }
                }

                try
                {
                    object coerced = CoerceValue(rawValue, targetType);
                    engObj.SetAttribute(attrName, coerced);
                    object newVal = null;
                    try { newVal = engObj.GetAttribute(attrName); } catch { }
                    okCount++;
                    results.Add(new {
                        name = attrName,
                        status = "ok",
                        oldValue = currentValue != null ? currentValue.ToString() : null,
                        newValue = newVal != null ? newVal.ToString() : null,
                        targetType = targetType != null ? targetType.FullName : null
                    });
                }
                catch (Exception ex)
                {
                    errCount++;
                    results.Add(new {
                        name = attrName,
                        status = "error",
                        error = ex.Message,
                        innerError = ex.InnerException != null ? ex.InnerException.Message : null,
                        targetType = targetType != null ? targetType.FullName : null
                    });
                }
            }

            SendJson(ctx.Response, 200, new { subnet = subnetName, okCount, errCount, results });
        }

        // 공용 Subnet 찾기
        static object FindSubnetByName(string projectName, string subnetName)
        {
            foreach (Project proj in connectedPortal.Projects)
            {
                if (!string.IsNullOrEmpty(projectName) && proj.Name != projectName) continue;
                foreach (var s in proj.Subnets)
                {
                    string n = null;
                    try { n = ((IEngineeringObject)s).GetAttribute("Name") as string; } catch { }
                    if (n == subnetName) return s;
                }
            }
            return null;
        }

        // 공용 attribute 덤프 (DeviceItem 외 Subnet 등 IEngineeringObject 전체에 사용)
        static List<object> ReadAllAttributesOf(IEngineeringObject obj)
        {
            var attributes = new List<object>();
            try
            {
                foreach (var info in obj.GetAttributeInfos())
                {
                    string val = null;
                    Type runtimeType = null;
                    try
                    {
                        object raw = obj.GetAttribute(info.Name);
                        val = raw != null ? raw.ToString() : null;
                        if (raw != null) runtimeType = raw.GetType();
                    }
                    catch { val = "<read error>"; }

                    string accessMode = null;
                    try
                    {
                        var amProp = info.GetType().GetProperty("AccessMode");
                        if (amProp != null)
                        {
                            var amVal = amProp.GetValue(info, null);
                            if (amVal != null) accessMode = amVal.ToString();
                        }
                    }
                    catch { }

                    Type declaredType = null;
                    try
                    {
                        var gtm = info.GetType().GetMethod("GetAttributeType");
                        if (gtm != null) declaredType = gtm.Invoke(info, null) as Type;
                    }
                    catch { }

                    // declared 타입이 null/object 면 runtime 타입으로 폴백
                    Type effectiveType = declaredType;
                    if (effectiveType == null || effectiveType == typeof(object)) effectiveType = runtimeType;

                    string typeName = null;
                    string[] enumValues = null;
                    bool isEnum = false;
                    if (effectiveType != null)
                    {
                        var underlying = Nullable.GetUnderlyingType(effectiveType);
                        var actual = underlying ?? effectiveType;
                        typeName = actual.Name;
                        if (actual.IsEnum)
                        {
                            isEnum = true;
                            try { enumValues = Enum.GetNames(actual); } catch { }
                        }
                    }

                    attributes.Add(new
                    {
                        name = info.Name,
                        value = val,
                        accessMode,
                        type = typeName,
                        isEnum,
                        enumValues,
                        declaredType = declaredType != null ? declaredType.FullName : null,
                        runtimeType = runtimeType != null ? runtimeType.FullName : null
                    });
                }
            }
            catch (Exception ex)
            {
                attributes.Add(new { name = "_error", value = ex.Message, accessMode = (string)null, type = (string)null, isEnum = false, enumValues = (string[])null, declaredType = (string)null, runtimeType = (string)null });
            }
            return attributes;
        }

        // ── Interface(Node) 를 특정 Subnet 에 연결. 존재하지 않으면 생성. ──
        // POST /api/network/attach  body: {project?, device, interfaceIndex, subnetName, createIfMissing?(default true)}
        static void AttachNodeToSubnet(HttpListenerContext ctx)
        {
            EnsureConnected();
            var body = ReadJsonBody(ctx);
            if (body == null) { SendJson(ctx.Response, 400, new { error = "Empty body" }); return; }

            string projectName = body.ContainsKey("project") ? body["project"] as string : null;
            string deviceName = body.ContainsKey("device") ? body["device"] as string : null;
            string subnetName = body.ContainsKey("subnetName") ? body["subnetName"] as string : null;
            int interfaceIndex = -1;
            if (body.ContainsKey("interfaceIndex"))
            {
                try { interfaceIndex = Convert.ToInt32(body["interfaceIndex"]); } catch { }
            }
            bool createIfMissing = true;
            if (body.ContainsKey("createIfMissing"))
            {
                try { createIfMissing = Convert.ToBoolean(body["createIfMissing"]); } catch { }
            }
            if (string.IsNullOrEmpty(deviceName) || string.IsNullOrEmpty(subnetName))
            {
                SendJson(ctx.Response, 400, new { error = "device, subnetName 필드 필요" });
                return;
            }

            Project foundProj = null; Device foundDev = null;
            foreach (Project proj in connectedPortal.Projects)
            {
                if (!string.IsNullOrEmpty(projectName) && proj.Name != projectName) continue;
                foreach (Device d in EnumerateAllDevices(proj))
                {
                    if (d.Name == deviceName) { foundProj = proj; foundDev = d; break; }
                }
                if (foundDev != null) break;
            }
            if (foundDev == null) { SendJson(ctx.Response, 404, new { error = "Device not found: " + deviceName }); return; }

            var ifs = FindAllNetworkInterfaces(foundDev);
            if (ifs.Count == 0) { SendJson(ctx.Response, 404, new { error = "NetworkInterface 없음" }); return; }
            if (interfaceIndex < 0 || interfaceIndex >= ifs.Count) interfaceIndex = 0;

            var netIf = ifs[interfaceIndex].Item1;
            Node node = null;
            try { foreach (Node n in netIf.Nodes) { node = n; break; } } catch { }
            if (node == null) { SendJson(ctx.Response, 404, new { error = "Node 없음" }); return; }

            // 기존 subnet 검색
            Subnet target = null;
            foreach (var s in foundProj.Subnets)
            {
                string sName = null;
                try { sName = ((IEngineeringObject)s).GetAttribute("Name") as string; } catch { }
                if (sName == subnetName) { target = (Subnet)s; break; }
            }

            string action = null;
            try
            {
                if (target == null)
                {
                    if (!createIfMissing) { SendJson(ctx.Response, 404, new { error = "Subnet not found, createIfMissing=false" }); return; }
                    node.CreateAndConnectToSubnet(subnetName);
                    action = "created";
                }
                else
                {
                    node.ConnectToSubnet(target);
                    action = "connected";
                }
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = action == null ? "Attach 실패: " + ex.Message : action + " 실패: " + ex.Message });
                return;
            }

            // 결과 확인
            string nowSubnet = null;
            try
            {
                var connProp = node.GetType().GetProperty("ConnectedSubnet");
                if (connProp != null)
                {
                    var connSub = connProp.GetValue(node, null);
                    if (connSub != null) nowSubnet = ((IEngineeringObject)connSub).GetAttribute("Name") as string;
                }
            }
            catch { }

            SendJson(ctx.Response, 200, new {
                project = foundProj.Name,
                device = deviceName,
                interfaceIndex,
                action,
                requestedSubnet = subnetName,
                connectedSubnet = nowSubnet,
                ok = nowSubnet == subnetName
            });
        }

        // ── Interface(Node) 를 현재 연결된 Subnet 에서 분리 ──
        // POST /api/network/detach  body: {project?, device, interfaceIndex}
        static void DetachNodeFromSubnet(HttpListenerContext ctx)
        {
            EnsureConnected();
            var body = ReadJsonBody(ctx);
            if (body == null) { SendJson(ctx.Response, 400, new { error = "Empty body" }); return; }

            string projectName = body.ContainsKey("project") ? body["project"] as string : null;
            string deviceName = body.ContainsKey("device") ? body["device"] as string : null;
            int interfaceIndex = -1;
            if (body.ContainsKey("interfaceIndex"))
            {
                try { interfaceIndex = Convert.ToInt32(body["interfaceIndex"]); } catch { }
            }
            if (string.IsNullOrEmpty(deviceName))
            {
                SendJson(ctx.Response, 400, new { error = "device 필드 필요" });
                return;
            }

            Device foundDev = null;
            foreach (Project proj in connectedPortal.Projects)
            {
                if (!string.IsNullOrEmpty(projectName) && proj.Name != projectName) continue;
                foreach (Device d in EnumerateAllDevices(proj))
                {
                    if (d.Name == deviceName) { foundDev = d; break; }
                }
                if (foundDev != null) break;
            }
            if (foundDev == null) { SendJson(ctx.Response, 404, new { error = "Device not found: " + deviceName }); return; }

            var ifs = FindAllNetworkInterfaces(foundDev);
            if (ifs.Count == 0) { SendJson(ctx.Response, 404, new { error = "NetworkInterface 없음" }); return; }
            if (interfaceIndex < 0 || interfaceIndex >= ifs.Count) interfaceIndex = 0;

            var netIf = ifs[interfaceIndex].Item1;
            Node node = null;
            try { foreach (Node n in netIf.Nodes) { node = n; break; } } catch { }
            if (node == null) { SendJson(ctx.Response, 404, new { error = "Node 없음" }); return; }

            string beforeSubnet = null;
            try
            {
                var connProp = node.GetType().GetProperty("ConnectedSubnet");
                if (connProp != null)
                {
                    var s = connProp.GetValue(node, null);
                    if (s != null) beforeSubnet = ((IEngineeringObject)s).GetAttribute("Name") as string;
                }
            }
            catch { }

            // DisconnectFromSubnet 호출 (Siemens.Engineering API)
            string usedMethod = null;
            Exception lastEx = null;
            try
            {
                var m = node.GetType().GetMethod("DisconnectFromSubnet", Type.EmptyTypes);
                if (m != null)
                {
                    m.Invoke(node, null);
                    usedMethod = "DisconnectFromSubnet";
                }
            }
            catch (System.Reflection.TargetInvocationException tie) { lastEx = tie.InnerException ?? tie; }
            catch (Exception ex) { lastEx = ex; }

            if (usedMethod == null && lastEx != null)
            {
                SendJson(ctx.Response, 500, new { error = "Detach 실패: " + lastEx.Message });
                return;
            }
            if (usedMethod == null)
            {
                SendJson(ctx.Response, 500, new { error = "DisconnectFromSubnet 메서드를 찾을 수 없음" });
                return;
            }

            string afterSubnet = null;
            try
            {
                var connProp = node.GetType().GetProperty("ConnectedSubnet");
                if (connProp != null)
                {
                    var s = connProp.GetValue(node, null);
                    if (s != null) afterSubnet = ((IEngineeringObject)s).GetAttribute("Name") as string;
                }
            }
            catch { }

            SendJson(ctx.Response, 200, new {
                device = deviceName,
                interfaceIndex,
                before = beforeSubnet,
                after = afterSubnet,
                ok = afterSubnet == null
            });
        }

        // body parse 헬퍼 (HttpListenerContext -> Dictionary<string, object>)
        static Dictionary<string, object> ReadJsonBody(HttpListenerContext ctx)
        {
            byte[] body;
            using (var ms = new MemoryStream())
            {
                ctx.Request.InputStream.CopyTo(ms);
                body = ms.ToArray();
            }
            string bodyStr = Encoding.UTF8.GetString(body);
            if (string.IsNullOrWhiteSpace(bodyStr)) return null;
            try { return json.Deserialize<Dictionary<string, object>>(bodyStr); }
            catch { return null; }
        }

        // DeviceItem에서 부모 Device까지 거슬러 올라가며 Name을 '/'로 연결
        static string BuildItemPath(DeviceItem di)
        {
            var parts = new List<string>();
            IEngineeringObject cur = di;
            while (cur is DeviceItem)
            {
                parts.Insert(0, ((DeviceItem)cur).Name);
                try { cur = cur.Parent; } catch { break; }
            }
            return string.Join("/", parts);
        }

        static object CoerceValue(object value, Type targetType)
        {
            if (value == null) return null;
            if (targetType == null) return value;
            if (targetType.IsInstanceOfType(value)) return value;

            // Nullable<T> 처리
            Type underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null) targetType = underlying;

            string str = value.ToString();

            if (targetType.IsEnum) return Enum.Parse(targetType, str, true);
            if (targetType == typeof(string)) return str;
            if (targetType == typeof(bool)) return bool.Parse(str);
            if (targetType == typeof(int)) return int.Parse(str);
            if (targetType == typeof(long)) return long.Parse(str);
            if (targetType == typeof(uint)) return uint.Parse(str);
            if (targetType == typeof(ulong)) return ulong.Parse(str);
            if (targetType == typeof(short)) return short.Parse(str);
            if (targetType == typeof(ushort)) return ushort.Parse(str);
            if (targetType == typeof(byte)) return byte.Parse(str);
            if (targetType == typeof(sbyte)) return sbyte.Parse(str);
            if (targetType == typeof(double)) return double.Parse(str, System.Globalization.CultureInfo.InvariantCulture);
            if (targetType == typeof(float)) return float.Parse(str, System.Globalization.CultureInfo.InvariantCulture);
            if (targetType == typeof(decimal)) return decimal.Parse(str, System.Globalization.CultureInfo.InvariantCulture);

            if (targetType == typeof(DateTime))
            {
                DateTime dt;
                // 1) ISO 8601 / round-trip (RoundtripKind는 Assume* 와 결합 불가)
                if (DateTime.TryParse(str, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out dt))
                    return dt;
                // 2) InvariantCulture 일반 파싱
                if (DateTime.TryParse(str, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out dt))
                    return dt;
                // 3) 현재 OS 문화권
                if (DateTime.TryParse(str, System.Globalization.CultureInfo.CurrentCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out dt))
                    return dt;
                // 4) 한국어 문화권 (오전/오후 명시 처리)
                if (DateTime.TryParse(str, new System.Globalization.CultureInfo("ko-KR"),
                        System.Globalization.DateTimeStyles.AssumeLocal, out dt))
                    return dt;
                throw new FormatException("DateTime 파싱 실패. ISO 8601(예: 2026-05-18T05:44:40) 또는 'yyyy-MM-dd HH:mm:ss' 형식을 사용하세요. 입력: " + str);
            }
            if (targetType == typeof(DateTimeOffset))
            {
                DateTimeOffset dto;
                if (DateTimeOffset.TryParse(str, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out dto)) return dto;
                if (DateTimeOffset.TryParse(str, System.Globalization.CultureInfo.CurrentCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out dto)) return dto;
                if (DateTimeOffset.TryParse(str, new System.Globalization.CultureInfo("ko-KR"),
                        System.Globalization.DateTimeStyles.AssumeLocal, out dto)) return dto;
                throw new FormatException("DateTimeOffset 파싱 실패. 입력: " + str);
            }
            if (targetType == typeof(TimeSpan))
            {
                TimeSpan ts;
                if (TimeSpan.TryParse(str, System.Globalization.CultureInfo.InvariantCulture, out ts)) return ts;
                if (TimeSpan.TryParse(str, System.Globalization.CultureInfo.CurrentCulture, out ts)) return ts;
                throw new FormatException("TimeSpan 파싱 실패. 입력: " + str);
            }

            return Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture);
        }

        static void GetDeviceSubItems(HttpListenerContext ctx)
        {
            string deviceName = ctx.Request.QueryString["device"];
            string itemPath = ctx.Request.QueryString["item"];
            string projectName = ctx.Request.QueryString["project"];
            int maxDepth = 2;
            string depthStr = ctx.Request.QueryString["depth"];
            if (depthStr != null) int.TryParse(depthStr, out maxDepth);

            EnsureConnected();

            if (string.IsNullOrEmpty(deviceName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?device= parameter" });
                return;
            }

            DeviceItemComposition startItems;
            string startName;

            if (string.IsNullOrEmpty(itemPath))
            {
                // Start from device root
                Device targetDevice = null;
                foreach (Project proj in connectedPortal.Projects)
                {
                    if (projectName != null && proj.Name != projectName) continue;
                    foreach (Device dev in proj.Devices)
                    {
                        if (dev.Name == deviceName) { targetDevice = dev; break; }
                    }
                    if (targetDevice != null) break;
                }
                if (targetDevice == null) { SendJson(ctx.Response, 404, new { error = "Device not found: " + deviceName }); return; }
                startItems = targetDevice.DeviceItems;
                startName = targetDevice.Name;
            }
            else
            {
                var di = FindDeviceItemByPath(deviceName, itemPath, projectName);
                if (di == null) { SendJson(ctx.Response, 404, new { error = "DeviceItem not found: " + itemPath }); return; }
                startItems = di.DeviceItems;
                startName = di.Name;
            }

            var items = CollectSubItemsWithAttributes(startItems, 0, maxDepth);
            SendJson(ctx.Response, 200, new { device = deviceName, root = startName, maxDepth, items });
        }

        static DeviceItem FindDeviceItemByPath(string deviceName, string itemPath, string projectName)
        {
            Device targetDevice = null;
            foreach (Project proj in connectedPortal.Projects)
            {
                if (projectName != null && proj.Name != projectName) continue;
                foreach (Device dev in EnumerateAllDevices(proj))
                {
                    if (dev.Name == deviceName) { targetDevice = dev; break; }
                }
                if (targetDevice != null) break;
            }
            if (targetDevice == null) return null;

            return FindInComposition(targetDevice.DeviceItems, itemPath);
        }

        // DeviceItem 이름에 '/'가 포함될 수 있으므로 greedy matching: 자식 이름 + '/' 로 시작하는 경우 그 접두사를 한 세그먼트로 간주
        static DeviceItem FindInComposition(DeviceItemComposition items, string remaining)
        {
            if (string.IsNullOrEmpty(remaining)) return null;

            // 1) remaining 전체가 자식 이름과 정확히 일치 (리프)
            foreach (DeviceItem di in items)
            {
                if (di.Name == remaining) return di;
            }

            // 2) 자식 이름 + '/' 가 remaining의 접두사인 경우 해당 자식 아래로 재귀
            foreach (DeviceItem di in items)
            {
                if (remaining.StartsWith(di.Name + "/"))
                {
                    string rest = remaining.Substring(di.Name.Length + 1);
                    var found = FindInComposition(di.DeviceItems, rest);
                    if (found != null) return found;
                }
            }
            return null;
        }

        static List<object> ReadAllAttributes(DeviceItem di)
        {
            var attributes = new List<object>();
            try
            {
                var attrInfos = ((IEngineeringObject)di).GetAttributeInfos();
                foreach (var info in attrInfos)
                {
                    string val = null;
                    try
                    {
                        object raw = ((IEngineeringObject)di).GetAttribute(info.Name);
                        val = raw != null ? raw.ToString() : null;
                    }
                    catch { val = "<read error>"; }

                    // AccessMode 추출 (Read / ReadWrite)
                    string accessMode = null;
                    try
                    {
                        var amProp = info.GetType().GetProperty("AccessMode");
                        if (amProp != null)
                        {
                            var amVal = amProp.GetValue(info, null);
                            if (amVal != null) accessMode = amVal.ToString();
                        }
                    }
                    catch { }

                    // 속성 타입 추출
                    string typeName = null;
                    try
                    {
                        var gtm = info.GetType().GetMethod("GetAttributeType");
                        if (gtm != null)
                        {
                            var t = gtm.Invoke(info, null) as Type;
                            if (t != null) typeName = t.Name;
                        }
                    }
                    catch { }

                    attributes.Add(new {
                        name = info.Name,
                        value = val,
                        accessMode,
                        type = typeName
                    });
                }
            }
            catch (Exception ex)
            {
                attributes.Add(new { name = "_error", value = ex.Message, accessMode = (string)null, type = (string)null });
            }
            return attributes;
        }

        static List<object> CollectSubItemsWithAttributes(DeviceItemComposition items, int depth, int maxDepth)
        {
            var result = new List<object>();
            foreach (DeviceItem di in items)
            {
                var attrs = ReadAllAttributes(di);
                var children = (depth < maxDepth) ? CollectSubItemsWithAttributes(di.DeviceItems, depth + 1, maxDepth) : new List<object>();

                result.Add(new {
                    name = di.Name,
                    type = di.TypeIdentifier,
                    classification = di.Classification.ToString(),
                    depth,
                    attributes = attrs,
                    children
                });
            }
            return result;
        }

        // ── GSD/GSDML API ──

        // 선택된 TIA 버전의 GSD 폴더. static 필드 초기화는 TiaBootstrap.selectedMajor 가
        // 정해지기 전에 돌 수 있으므로 프로퍼티로 둔다.
        static string gsdBasePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"Siemens\Automation\Portal V" + TiaBootstrap.selectedMajor + @"\data\xdd\gsd");
            }
        }

        static void ListGsdFiles(HttpListenerContext ctx)
        {
            try
            {
                string searchPath = ctx.Request.QueryString["path"] ?? gsdBasePath;
                if (!Directory.Exists(searchPath))
                {
                    SendJson(ctx.Response, 404, new { error = "GSD directory not found", path = searchPath });
                    return;
                }

                var result = new List<object>();
                foreach (var dir in Directory.GetDirectories(searchPath))
                {
                    string dirName = Path.GetFileName(dir);
                    var xmlFiles = Directory.GetFiles(dir, "*.xml");
                    foreach (var xmlFile in xmlFiles)
                    {
                        string fileName = Path.GetFileName(xmlFile);
                        if (!fileName.ToLowerInvariant().StartsWith("gsdml")) continue;

                        var fi = new FileInfo(xmlFile);
                        // Extract vendor from filename: GSDML-V2.32-KEYENCE-SR-2000-20170110.xml
                        string vendor = "Unknown";
                        string[] parts = fileName.Replace(".xml", "").Replace(".XML", "").Split('-');
                        if (parts.Length >= 3) vendor = parts[2];

                        result.Add(new Dictionary<string, object> {
                            { "fileName", fileName },
                            { "directory", dirName },
                            { "fullPath", xmlFile },
                            { "vendor", vendor },
                            { "size", fi.Length },
                            { "lastModified", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") }
                        });
                    }
                }
                SendJson(ctx.Response, 200, new { count = result.Count, basePath = searchPath, files = result });
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Failed to list GSD files", detail = ex.Message });
            }
        }

        static void ReadGsdFile(HttpListenerContext ctx)
        {
            try
            {
                string file = ctx.Request.QueryString["file"];
                if (string.IsNullOrEmpty(file))
                {
                    SendJson(ctx.Response, 400, new { error = "?file= 파라미터가 필요합니다 (GSDML 파일명 또는 전체 경로)" });
                    return;
                }

                string fullPath = file;
                // If just filename, search in gsd base path
                if (!File.Exists(fullPath))
                {
                    // Try searching in subdirectories
                    if (Directory.Exists(gsdBasePath))
                    {
                        var found = Directory.GetFiles(gsdBasePath, file, SearchOption.AllDirectories);
                        if (found.Length > 0) fullPath = found[0];
                    }
                }

                if (!File.Exists(fullPath))
                {
                    SendJson(ctx.Response, 404, new { error = "GSDML file not found", file = file });
                    return;
                }

                byte[] content = File.ReadAllBytes(fullPath);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/xml; charset=utf-8";
                ctx.Response.ContentLength64 = content.Length;
                ctx.Response.Headers.Add("Content-Disposition", "inline; filename=\"" + Path.GetFileName(fullPath) + "\"");
                ctx.Response.OutputStream.Write(content, 0, content.Length);
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Failed to read GSD file", detail = ex.Message });
            }
        }

        static void ParseGsdmlToCatalog(HttpListenerContext ctx)
        {
            try
            {
                // Read GSDML XML from request body
                XDocument doc;
                using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    doc = XDocument.Parse(sr.ReadToEnd());
                }

                // Query params
                int startCatalogId = 10001;
                int startDeviceId = 10001;
                string qCatalogId = ctx.Request.QueryString["startCatalogId"];
                string qDeviceId = ctx.Request.QueryString["startDeviceId"];
                if (qCatalogId != null) int.TryParse(qCatalogId, out startCatalogId);
                if (qDeviceId != null) int.TryParse(qDeviceId, out startDeviceId);
                int parentCatalogId = 3285;
                string qParentId = ctx.Request.QueryString["parentCatalogId"];
                if (qParentId != null) int.TryParse(qParentId, out parentCatalogId);

                XNamespace ns = doc.Root.Name.Namespace;

                // Parse GSDML structure
                var profileBody = doc.Root.Element(ns + "ProfileBody");
                if (profileBody == null)
                {
                    SendJson(ctx.Response, 400, new { error = "Invalid GSDML: ProfileBody not found" });
                    return;
                }

                // DeviceIdentity
                var deviceIdentity = profileBody.Element(ns + "DeviceIdentity");
                string vendorName = deviceIdentity?.Element(ns + "VendorName")?.Attribute("Value")?.Value ?? "Unknown";
                // Clean vendor name (remove trailing dots/spaces)
                vendorName = vendorName.TrimEnd('.', ' ');

                // DeviceFunction > Family
                var deviceFunction = profileBody.Element(ns + "DeviceFunction");
                var family = deviceFunction?.Element(ns + "Family");
                string mainFamily = family?.Attribute("MainFamily")?.Value ?? "PROFINET IO";
                string productFamily = family?.Attribute("ProductFamily")?.Value ?? vendorName;

                var appProcess = profileBody.Element(ns + "ApplicationProcess");
                if (appProcess == null)
                {
                    SendJson(ctx.Response, 400, new { error = "Invalid GSDML: ApplicationProcess not found" });
                    return;
                }

                // Build TextId → Value lookup from ExternalTextList
                var textMap = new Dictionary<string, string>();
                var extTextList = appProcess.Element(ns + "ExternalTextList");
                if (extTextList != null)
                {
                    var primaryLang = extTextList.Element(ns + "PrimaryLanguage");
                    if (primaryLang != null)
                    {
                        foreach (var text in primaryLang.Elements(ns + "Text"))
                        {
                            string tid = text.Attribute("TextId")?.Value;
                            string val = text.Attribute("Value")?.Value;
                            if (tid != null && val != null) textMap[tid] = val;
                        }
                    }
                }
                // Helper to resolve TextId
                Func<string, string> resolveText = (textId) => {
                    if (textId != null && textMap.ContainsKey(textId)) return textMap[textId];
                    return textId;
                };

                // Extract GSDML filename from the doc (for type_identifier construction)
                // Try to get from xsi:schemaLocation or filename pattern
                string gsdFileName = "";
                var schemaLoc = doc.Root.Attribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "schemaLocation")?.Value ?? "";
                // Fallback: use header hint or reconstruct from vendor info
                string headerFileName = ctx.Request.Headers["X-Filename"];
                if (!string.IsNullOrEmpty(headerFileName))
                {
                    gsdFileName = Path.GetFileNameWithoutExtension(headerFileName).ToUpperInvariant() + ".XML";
                }

                // Parse DeviceAccessPointList (DAPs)
                var dapList = appProcess.Element(ns + "DeviceAccessPointList");
                if (dapList == null)
                {
                    SendJson(ctx.Response, 400, new { error = "Invalid GSDML: DeviceAccessPointList not found" });
                    return;
                }

                // Parse ModuleList (all available modules)
                var moduleList = appProcess.Element(ns + "ModuleList");
                var moduleMap = new Dictionary<string, XElement>(); // ID → ModuleItem
                if (moduleList != null)
                {
                    foreach (var mod in moduleList.Elements(ns + "ModuleItem"))
                    {
                        string modId = mod.Attribute("ID")?.Value;
                        if (modId != null) moduleMap[modId] = mod;
                    }
                }

                // Build result
                int catalogId = startCatalogId;
                int deviceId = startDeviceId;

                var hwCatalog = new List<object>();
                var hwDevice = new List<object>();
                var hwVersion = new List<object>();
                var hwInterface = new List<object>();
                var hwPort = new List<object>();
                var hwRackMapping = new List<object>();
                var hwSlot = new List<object>();

                // Vendor catalog entry
                int vendorCatalogId = catalogId++;
                hwCatalog.Add(new Dictionary<string, object> {
                    { "catalog_id", vendorCatalogId },
                    { "parent_id", parentCatalogId },
                    { "name", vendorName },
                    { "depth", 3 }
                });

                // Product family catalog entry
                int familyCatalogId = catalogId++;
                hwCatalog.Add(new Dictionary<string, object> {
                    { "catalog_id", familyCatalogId },
                    { "parent_id", vendorCatalogId },
                    { "name", productFamily },
                    { "depth", 4 }
                });

                // Process each DAP
                foreach (var dap in dapList.Elements(ns + "DeviceAccessPointItem"))
                {
                    string dapId = dap.Attribute("ID")?.Value ?? "DAP1";
                    string physSlots = dap.Attribute("PhysicalSlots")?.Value ?? "";
                    string dnsName = dap.Attribute("DNS_CompatibleName")?.Value ?? "";

                    // ModuleInfo for DAP
                    var dapModInfo = dap.Element(ns + "ModuleInfo");
                    string dapNameTextId = dapModInfo?.Element(ns + "Name")?.Attribute("TextId")?.Value;
                    string dapInfoTextId = dapModInfo?.Element(ns + "InfoText")?.Attribute("TextId")?.Value;
                    string dapName = resolveText(dapNameTextId);
                    string dapDescription = resolveText(dapInfoTextId);
                    string orderNumber = dapModInfo?.Element(ns + "OrderNumber")?.Attribute("Value")?.Value ?? dapName;

                    // Construct type_identifier: GSD:GSDML-V2.32-KEYENCE-SR-2000-20170110.XML/DAP/DAP1
                    string dapTypeId = "GSD:" + gsdFileName + "/DAP/" + dapId;
                    string deviceTypeId = "GSD:" + gsdFileName + "/D";
                    string rackTypeId = "GSD:" + gsdFileName + "/D";

                    int thisDeviceId = deviceId++;

                    // hw_device
                    hwDevice.Add(new Dictionary<string, object> {
                        { "hw_device_id", thisDeviceId },
                        { "catalog_id", familyCatalogId },
                        { "name", vendorName + " " + (orderNumber ?? dapName) },
                        { "article_number", orderNumber ?? dapName }
                    });

                    // hw_version - DAP
                    hwVersion.Add(new Dictionary<string, object> {
                        { "type_identifier", dapTypeId },
                        { "hw_device_id", thisDeviceId },
                        { "version", "(" + gsdFileName + ")" },
                        { "type_identifier_norm", dapTypeId },
                        { "description", dapDescription ?? (dapName + " with PROFINET interface") }
                    });

                    // Interface and Ports from SystemDefinedSubmoduleList
                    var sysDefined = dap.Element(ns + "SystemDefinedSubmoduleList");
                    int portCount = 0;
                    if (sysDefined != null)
                    {
                        // Interface
                        var ifaceItem = sysDefined.Element(ns + "InterfaceSubmoduleItem");
                        if (ifaceItem != null)
                        {
                            string ifaceTextId = ifaceItem.Attribute("TextId")?.Value;
                            string ifaceName = resolveText(ifaceTextId);

                            // Count ports
                            var portItems = sysDefined.Elements(ns + "PortSubmoduleItem").ToList();
                            portCount = portItems.Count;

                            // Also find interface label from SubslotList
                            string ifaceLabel = "X1";
                            var subslotList = dap.Element(ns + "SubslotList");
                            if (subslotList != null)
                            {
                                var firstSubslot = subslotList.Elements(ns + "SubslotItem").FirstOrDefault();
                                if (firstSubslot != null)
                                {
                                    string slotTextId = firstSubslot.Attribute("TextId")?.Value;
                                    string slotText = resolveText(slotTextId);
                                    if (slotText != null && !slotText.Contains(" ")) ifaceLabel = slotText;
                                }
                            }

                            hwInterface.Add(new Dictionary<string, object> {
                                { "interface_num", 1 },
                                { "type_identifier", dapTypeId },
                                { "interface_label", ifaceLabel },
                                { "name", ifaceName ?? "Interface" },
                                { "type", "Ethernet" },
                                { "port_count", portCount }
                            });

                            // Ports
                            int portNum = 1;
                            foreach (var portItem in portItems)
                            {
                                string portTextId = portItem.Attribute("TextId")?.Value;
                                string portName = resolveText(portTextId);

                                // Find port label from SubslotList
                                string portLabel = "P" + portNum;
                                if (subslotList != null)
                                {
                                    int subslotNum = 0;
                                    string ssStr = portItem.Attribute("SubslotNumber")?.Value;
                                    if (ssStr != null) int.TryParse(ssStr, out subslotNum);
                                    var matchingSubslot = subslotList.Elements(ns + "SubslotItem")
                                        .FirstOrDefault(s => s.Attribute("SubslotNumber")?.Value == ssStr);
                                    if (matchingSubslot != null)
                                    {
                                        string pTextId = matchingSubslot.Attribute("TextId")?.Value;
                                        string pText = resolveText(pTextId);
                                        // Extract label like "X1 P1" → "P1", or "P1" directly
                                        if (pText != null)
                                        {
                                            if (pText.Contains(" "))
                                                portLabel = pText.Substring(pText.LastIndexOf(' ') + 1);
                                            else
                                                portLabel = pText;
                                        }
                                    }
                                }

                                hwPort.Add(new Dictionary<string, object> {
                                    { "port_num", portNum },
                                    { "interface_num", 1 },
                                    { "type_identifier", dapTypeId },
                                    { "port_label", portLabel },
                                    { "port_name", portName ?? ("Port " + portNum) }
                                });
                                portNum++;
                            }
                        }
                    }

                    // hw_rack_mapping
                    hwRackMapping.Add(new Dictionary<string, object> {
                        { "type_identifier", dapTypeId },
                        { "rack_id", rackTypeId },
                        { "name", "GSD device_1" }
                    });

                    // hw_slot (slot 0 = head unit)
                    hwSlot.Add(new Dictionary<string, object> {
                        { "slot_number", 0 },
                        { "type_identifier", dapTypeId },
                        { "rack_id", rackTypeId },
                        { "is_head_unit", true }
                    });

                    // Modules from UseableModules → ModuleList
                    var useableModules = dap.Element(ns + "UseableModules");
                    if (useableModules != null)
                    {
                        foreach (var modRef in useableModules.Elements(ns + "ModuleItemRef"))
                        {
                            string modTarget = modRef.Attribute("ModuleItemTarget")?.Value;
                            if (modTarget == null || !moduleMap.ContainsKey(modTarget)) continue;

                            var modItem = moduleMap[modTarget];
                            var modInfo = modItem.Element(ns + "ModuleInfo");
                            string modNameTextId = modInfo?.Element(ns + "Name")?.Attribute("TextId")?.Value;
                            string modName = resolveText(modNameTextId);

                            // Construct module type_identifier: GSD:filename.XML/M/101
                            string modTypeId = "GSD:" + gsdFileName + "/M/" + modTarget;

                            // Calculate IO data size
                            var ioDataList = new List<object>();
                            var vSubList = modItem.Element(ns + "VirtualSubmoduleList");
                            if (vSubList != null)
                            {
                                foreach (var vSub in vSubList.Elements(ns + "VirtualSubmoduleItem"))
                                {
                                    var ioData = vSub.Element(ns + "IOData");
                                    if (ioData == null) continue;

                                    foreach (var direction in new[] { "Input", "Output" })
                                    {
                                        var dirEl = ioData.Element(ns + direction);
                                        if (dirEl == null) continue;
                                        int totalBits = 0;
                                        foreach (var dataItem in dirEl.Elements(ns + "DataItem"))
                                        {
                                            string dataType = dataItem.Attribute("DataType")?.Value ?? "";
                                            string lengthStr = dataItem.Attribute("Length")?.Value;
                                            totalBits += CalcDataTypeBits(dataType, lengthStr);
                                        }
                                        if (totalBits > 0)
                                        {
                                            ioDataList.Add(new Dictionary<string, object> {
                                                { "length", totalBits },
                                                { "ioType", direction }
                                            });
                                        }
                                    }
                                }
                            }

                            hwVersion.Add(new Dictionary<string, object> {
                                { "type_identifier", modTypeId },
                                { "hw_device_id", thisDeviceId },
                                { "version", "(" + gsdFileName + ")" },
                                { "type_identifier_norm", modTypeId },
                                { "description", modName ?? modTarget },
                                { "io_data", ioDataList }
                            });
                        }
                    }
                }

                var result = new Dictionary<string, object> {
                    { "_description", "GSDML에서 추출된 GSD 디바이스 카탈로그 데이터 (스펙 기준 전체 모듈 포함)" },
                    { "_source", gsdFileName },
                    { "_date", DateTime.Now.ToString("yyyy-MM-dd") },
                    { "_vendor", vendorName },
                    { "_productFamily", productFamily },
                    { "hw_catalog", hwCatalog },
                    { "hw_device", hwDevice },
                    { "hw_version", hwVersion },
                    { "hw_interface", hwInterface },
                    { "hw_port", hwPort },
                    { "hw_rack_mapping", hwRackMapping },
                    { "hw_slot", hwSlot }
                };

                SendJson(ctx.Response, 200, result);
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "GSDML parsing failed", detail = ex.Message });
            }
        }

        static int CalcDataTypeBits(string dataType, string lengthAttr)
        {
            if (!string.IsNullOrEmpty(lengthAttr))
            {
                int len;
                if (int.TryParse(lengthAttr, out len)) return len * 8;
            }
            switch (dataType)
            {
                case "Boolean": return 1;
                case "Integer8": case "Unsigned8": return 8;
                case "Integer16": case "Unsigned16": return 16;
                case "Integer32": case "Unsigned32": case "Float32": return 32;
                case "Integer64": case "Unsigned64": case "Float64": return 64;
                case "OctetString": case "VisibleString":
                    return 8; // default 1 byte if no length
                default: return 8;
            }
        }

        // ── AML (CAx) Export / Import ──

        static void ExportAml(HttpListenerContext ctx)
        {
            string projectName = ctx.Request.QueryString["project"];
            EnsureConnected();

            Project project = null;
            foreach (Project p in connectedPortal.Projects)
            {
                if (projectName == null || p.Name == projectName) { project = p; break; }
            }
            if (project == null) { SendJson(ctx.Response, 404, new { error = "No project found" }); return; }

            try
            {
                string exportDir = Path.Combine(tempDir, "aml_export");
                if (Directory.Exists(exportDir)) Directory.Delete(exportDir, true);
                Directory.CreateDirectory(exportDir);

                string amlPath = Path.Combine(exportDir, project.Name + ".aml");
                var caxProvider = project.GetService<CaxProvider>();
                if (caxProvider == null)
                {
                    SendJson(ctx.Response, 500, new { error = "CaxProvider service not available for this project" });
                    return;
                }

                string logPath = Path.Combine(exportDir, project.Name + "_export.log");
                bool success = caxProvider.Export(project, new FileInfo(amlPath), new FileInfo(logPath));

                string logContent = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
                Console.WriteLine("AML exported: " + amlPath + " success=" + success);

                if (!File.Exists(amlPath))
                {
                    SendJson(ctx.Response, 500, new { error = "AML export failed — no file generated", log = logContent });
                    return;
                }

                // ZIP으로 묶어서 전송 (aml + log)
                string zipPath = Path.Combine(exportDir, project.Name + "_aml.zip");
                if (File.Exists(zipPath)) File.Delete(zipPath);
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    zip.CreateEntryFromFile(amlPath, Path.GetFileName(amlPath));
                    if (File.Exists(logPath))
                        zip.CreateEntryFromFile(logPath, Path.GetFileName(logPath));
                }

                var resp = ctx.Response;
                resp.Headers.Add("X-Export-Success", success.ToString());
                resp.Headers.Add("Access-Control-Expose-Headers", "X-Export-Success");
                SendFile(resp, zipPath, project.Name + "_aml.zip");
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "AML export failed: " + ex.Message });
            }
        }

        static void ExportDeviceAml(HttpListenerContext ctx)
        {
            string projectName = ctx.Request.QueryString["project"];
            string deviceName = ctx.Request.QueryString["device"];
            if (string.IsNullOrEmpty(deviceName))
            {
                SendJson(ctx.Response, 400, new { error = "device parameter is required" });
                return;
            }
            EnsureConnected();

            Project project = null;
            foreach (Project p in connectedPortal.Projects)
            {
                if (projectName == null || p.Name == projectName) { project = p; break; }
            }
            if (project == null) { SendJson(ctx.Response, 404, new { error = "No project found" }); return; }

            Device targetDevice = null;
            foreach (Device d in project.Devices)
            {
                if (d.Name == deviceName) { targetDevice = d; break; }
            }
            if (targetDevice == null)
            {
                SendJson(ctx.Response, 404, new { error = "Device not found: " + deviceName });
                return;
            }

            try
            {
                string exportDir = Path.Combine(tempDir, "aml_device_export");
                if (Directory.Exists(exportDir)) Directory.Delete(exportDir, true);
                Directory.CreateDirectory(exportDir);

                string amlPath = Path.Combine(exportDir, deviceName + ".aml");
                string logPath = Path.Combine(exportDir, deviceName + "_export.log");

                var caxProvider = project.GetService<CaxProvider>();
                if (caxProvider == null)
                {
                    SendJson(ctx.Response, 500, new { error = "CaxProvider service not available" });
                    return;
                }

                bool success = caxProvider.Export(targetDevice, new FileInfo(amlPath), new FileInfo(logPath));

                string logContent = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
                Console.WriteLine("Device AML exported: " + deviceName + " success=" + success);

                if (!File.Exists(amlPath))
                {
                    SendJson(ctx.Response, 500, new { error = "Device AML export failed — no file generated", success = false, log = logContent });
                    return;
                }

                // ZIP으로 묶어서 전송 (aml + log)
                string zipPath = Path.Combine(exportDir, deviceName + "_aml.zip");
                if (File.Exists(zipPath)) File.Delete(zipPath);
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    zip.CreateEntryFromFile(amlPath, Path.GetFileName(amlPath));
                    if (File.Exists(logPath))
                        zip.CreateEntryFromFile(logPath, Path.GetFileName(logPath));
                }

                var resp = ctx.Response;
                resp.Headers.Add("X-Export-Success", success.ToString());
                resp.Headers.Add("X-Device-Name", deviceName);
                resp.Headers.Add("Access-Control-Expose-Headers", "X-Export-Success, X-Device-Name");
                SendFile(resp, zipPath, deviceName + "_aml.zip");
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Device AML export failed: " + ex.Message });
            }
        }

        static void ImportAml(HttpListenerContext ctx)
        {
            string projectName = ctx.Request.QueryString["project"];
            string filePath = ctx.Request.QueryString["file"];
            string optionStr = ctx.Request.QueryString["option"] ?? "MoveToParkingLot";
            EnsureConnected();

            Project project = null;
            foreach (Project p in connectedPortal.Projects)
            {
                if (projectName == null || p.Name == projectName) { project = p; break; }
            }
            if (project == null) { SendJson(ctx.Response, 404, new { error = "No project found" }); return; }

            try
            {
                string amlFilePath;

                if (!string.IsNullOrEmpty(filePath))
                {
                    // 파일 경로로 직접 import
                    if (!File.Exists(filePath))
                    {
                        SendJson(ctx.Response, 404, new { error = "File not found: " + filePath });
                        return;
                    }
                    amlFilePath = filePath;
                }
                else
                {
                    // POST body에서 업로드
                    if (ctx.Request.ContentLength64 <= 0)
                    {
                        SendJson(ctx.Response, 400, new { error = "Provide 'file' query parameter or upload AML file in POST body" });
                        return;
                    }

                    string uploadDir = Path.Combine(tempDir, "aml_import");
                    if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);

                    amlFilePath = Path.Combine(uploadDir, "upload_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".aml");
                    using (var fs = new FileStream(amlFilePath, FileMode.Create))
                    {
                        ctx.Request.InputStream.CopyTo(fs);
                    }
                }

                var caxProvider = project.GetService<CaxProvider>();
                if (caxProvider == null)
                {
                    SendJson(ctx.Response, 500, new { error = "CaxProvider service not available for this project" });
                    return;
                }

                string logDir = Path.Combine(tempDir, "aml_import");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, "import_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");

                CaxImportOptions importOption;
                switch (optionStr)
                {
                    case "OverwriteTiaDevice": importOption = CaxImportOptions.OverwriteTiaDevice; break;
                    case "RetainTiaDevice": importOption = CaxImportOptions.RetainTiaDevice; break;
                    default: importOption = CaxImportOptions.MoveToParkingLot; break;
                }

                bool success = caxProvider.Import(new FileInfo(amlFilePath), new FileInfo(logPath), importOption);

                string logContent = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
                Console.WriteLine("AML imported: " + amlFilePath + " success=" + success);

                SendJson(ctx.Response, 200, new
                {
                    message = success ? "AML imported successfully" : "AML import completed with warnings",
                    success = success,
                    project = project.Name,
                    file = amlFilePath,
                    option = importOption.ToString(),
                    log = logContent
                });
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "AML import failed: " + ex.Message });
            }
        }

        // ── Device Export (hardware configuration as JSON ZIP) ──

        static void ExportDeviceConfig(HttpListenerContext ctx)
        {
            string projectName = ctx.Request.QueryString["project"];
            EnsureConnected();

            Project project = null;
            foreach (Project p in connectedPortal.Projects)
            {
                if (projectName == null || p.Name == projectName) { project = p; break; }
            }
            if (project == null) { SendJson(ctx.Response, 404, new { error = "No project found" }); return; }

            try
            {
                // 1. Collect devices
                var devicesList = new List<Dictionary<string, object>>();
                var deviceIndexMap = new Dictionary<string, int>(); // device.Name -> index (for topology)
                int deviceIdx = 0;

                foreach (Device device in project.Devices)
                {
                    string deviceId = "dev_" + deviceIdx;
                    deviceIndexMap[device.Name] = deviceIdx;

                    // Find slots (plugged modules)
                    var slots = CollectPluggedModules(device);

                    var devDict = new Dictionary<string, object>
                    {
                        { "deviceInstanceId", deviceId },
                        { "name", device.Name },
                        { "typeIdentifier", device.TypeIdentifier ?? "" },
                        { "positionX", "0" },
                        { "positionY", (deviceIdx * 100).ToString() },
                        { "slots", slots }
                    };
                    devicesList.Add(devDict);
                    deviceIdx++;
                }

                // 2. Collect subnets
                var subnetsList = new List<Dictionary<string, object>>();
                var subnetNameToId = new Dictionary<string, string>();
                int subnetIdx = 0;
                foreach (var subnet in project.Subnets)
                {
                    string subnetId = "subnet_" + subnetIdx;
                    string subnetName = "";
                    string subnetType = "Ethernet";
                    try { subnetName = ((IEngineeringObject)subnet).GetAttribute("Name") as string ?? "Subnet_" + subnetIdx; } catch { subnetName = "Subnet_" + subnetIdx; }
                    try { subnetType = ((IEngineeringObject)subnet).GetAttribute("TypeIdentifier") as string ?? "Ethernet"; } catch { }
                    // Normalize type: "System:Subnet.Ethernet" -> "Ethernet"
                    if (subnetType.Contains(".")) subnetType = subnetType.Substring(subnetType.LastIndexOf('.') + 1);

                    subnetNameToId[subnetName] = subnetId;

                    subnetsList.Add(new Dictionary<string, object>
                    {
                        { "subnetId", subnetId },
                        { "subnetName", subnetName },
                        { "subnetType", subnetType }
                    });
                    subnetIdx++;
                }

                // 3. Collect subnet configs (network interface IP/mask per device)
                var subnetConfigsList = new List<Dictionary<string, object>>();
                var ioSystemsList = new List<Dictionary<string, object>>();
                var topologyList = new List<Dictionary<string, object>>();
                int scIdx = 0;
                // Map: deviceId + interfaceNum -> subnetConfigId (for IO system linking)
                var scMap = new Dictionary<string, string>();

                foreach (Device device in project.Devices)
                {
                    string deviceId = "dev_" + deviceIndexMap[device.Name];
                    int interfaceNum = 0;

                    var interfaces = FindAllNetworkInterfaces(device);
                    foreach (var ifInfo in interfaces)
                    {
                        interfaceNum++;
                        var netIf = ifInfo.Item1; // NetworkInterface
                        var diItem = ifInfo.Item2; // DeviceItem

                        string scId = "sc_" + scIdx;
                        string address = "";
                        string subnetMask = "";
                        string connectedSubnetId = "";
                        string ioSystemName = "";

                        // Read node attributes (IP, subnet mask, connected subnet)
                        if (netIf.Nodes.Count > 0)
                        {
                            var node = netIf.Nodes[0];
                            try { address = node.GetAttribute("Address") as string ?? ""; } catch { }
                            try { subnetMask = node.GetAttribute("SubnetMask") as string ?? ""; } catch { }

                            // Find connected subnet
                            try
                            {
                                var connSubnet = node.GetType().GetProperty("ConnectedSubnet")?.GetValue(node);
                                if (connSubnet != null)
                                {
                                    string connSubnetName = ((IEngineeringObject)connSubnet).GetAttribute("Name") as string;
                                    if (connSubnetName != null && subnetNameToId.ContainsKey(connSubnetName))
                                        connectedSubnetId = subnetNameToId[connSubnetName];
                                }
                            }
                            catch { }
                        }

                        // Check for IoController (master) - IO system info
                        try
                        {
                            if (netIf.IoControllers.Count > 0)
                            {
                                var ioCtl = netIf.IoControllers[0];
                                if (ioCtl.IoSystem != null)
                                {
                                    try { ioSystemName = ((IEngineeringObject)ioCtl.IoSystem).GetAttribute("Name") as string ?? ""; } catch { }

                                    // Collect IO slaves
                                    try
                                    {
                                        var ioSystem = ioCtl.IoSystem;
                                        var ioDevicesProp = ioSystem.GetType().GetProperty("IoDevices");
                                        var ioDevicesEnum = ioDevicesProp?.GetValue(ioSystem) as System.Collections.IEnumerable;
                                        if (ioDevicesEnum != null)
                                        foreach (var ioDevice in ioDevicesEnum)
                                        {
                                            // Find slave device name and map to deviceId
                                            string slaveName = "";
                                            try
                                            {
                                                // Navigate via reflection: IoDevice -> Device property
                                                var slaveDevice = ioDevice.GetType().GetProperty("Device")?.GetValue(ioDevice);
                                                if (slaveDevice != null)
                                                    slaveName = ((IEngineeringObject)slaveDevice).GetAttribute("Name") as string ?? "";
                                            }
                                            catch
                                            {
                                                try { slaveName = ((IEngineeringObject)ioDevice).GetAttribute("Name") as string ?? ""; } catch { }
                                            }

                                            if (deviceIndexMap.ContainsKey(slaveName))
                                            {
                                                ioSystemsList.Add(new Dictionary<string, object>
                                                {
                                                    { "slaveDeviceInstanceId", "dev_" + deviceIndexMap[slaveName] },
                                                    { "masterNetworkId", scId },
                                                    { "masterDeviceInstanceId", deviceId },
                                                    { "masterInterfaceNum", interfaceNum },
                                                    { "ioSystemName", ioSystemName }
                                                });
                                            }
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }

                        scMap[deviceId + "_" + interfaceNum] = scId;

                        subnetConfigsList.Add(new Dictionary<string, object>
                        {
                            { "subnetConfigId", scId },
                            { "subnetId", connectedSubnetId },
                            { "deviceInstanceId", deviceId },
                            { "interfaceNum", interfaceNum },
                            { "address", address },
                            { "subnetMask", subnetMask },
                            { "ioSystemName", ioSystemName }
                        });
                        scIdx++;

                        // 4. Collect topology (port connections)
                        try
                        {
                            foreach (var port in FindNetworkPorts(diItem))
                            {
                                var netPort = port.Item1;
                                int portNum = port.Item2;
                                try
                                {
                                    var connectedPort = netPort.GetType().GetProperty("ConnectedPort")?.GetValue(netPort);
                                    if (connectedPort != null)
                                    {
                                        // Find the remote device
                                        DeviceItem remoteItem = connectedPort.GetType().GetProperty("DeviceItem")?.GetValue(connectedPort) as DeviceItem;
                                        if (remoteItem != null)
                                        {
                                            // Walk up to find Device
                                            var remoteDevice = FindParentDevice(remoteItem);
                                            if (remoteDevice != null && deviceIndexMap.ContainsKey(remoteDevice.Name))
                                            {
                                                string toDeviceId = "dev_" + deviceIndexMap[remoteDevice.Name];
                                                // Find remote interface/port num
                                                var remotePortInfo = GetPortPosition(remoteDevice, connectedPort);

                                                // Avoid duplicates: only add if from < to
                                                int fromIdx = deviceIndexMap[device.Name];
                                                int toIdx = deviceIndexMap[remoteDevice.Name];
                                                if (fromIdx < toIdx)
                                                {
                                                    topologyList.Add(new Dictionary<string, object>
                                                    {
                                                        { "topologyId", "topo_" + topologyList.Count },
                                                        { "fromDeviceInstanceId", deviceId },
                                                        { "fromInterfaceNum", interfaceNum },
                                                        { "fromPortNum", portNum },
                                                        { "toDeviceInstanceId", toDeviceId },
                                                        { "toInterfaceNum", remotePortInfo.Item1 },
                                                        { "toPortNum", remotePortInfo.Item2 }
                                                    });
                                                }
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }

                // 5. Build manifest
                var manifest = new Dictionary<string, object>
                {
                    { "version", "1.0" },
                    { "projectName", project.Name },
                    { "exportDate", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") },
                    { "source", "TiaApiServer" },
                    { "hardware", new Dictionary<string, object>
                        {
                            { "devices", "hardware/devices.json" },
                            { "subnets", "hardware/subnets.json" },
                            { "subnetConfigs", "hardware/subnet_configs.json" },
                            { "ioSystems", "hardware/io_systems.json" },
                            { "topology", "hardware/topology.json" }
                        }
                    },
                    { "software", new Dictionary<string, object>() },
                    { "importOrder", new[] {
                        "hardware.devices", "hardware.subnets", "hardware.subnetConfigs",
                        "hardware.ioSystems", "hardware.topology",
                        "software.types", "software.tags", "software.blocks"
                    }},
                    { "summary", new Dictionary<string, object>
                        {
                            { "deviceCount", devicesList.Count },
                            { "subnetCount", subnetsList.Count },
                            { "subnetConfigCount", subnetConfigsList.Count },
                            { "ioSystemCount", ioSystemsList.Count },
                            { "topologyCount", topologyList.Count }
                        }
                    }
                };

                // 6. Create ZIP
                string zipPath = Path.Combine(tempDir, "device_export_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip");
                Directory.CreateDirectory(tempDir);

                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    WriteJsonEntry(zip, "manifest.json", manifest);
                    WriteJsonEntry(zip, "hardware/devices.json", new Dictionary<string, object> { { "devices", devicesList } });
                    WriteJsonEntry(zip, "hardware/subnets.json", new Dictionary<string, object> { { "subnets", subnetsList } });
                    WriteJsonEntry(zip, "hardware/subnet_configs.json", new Dictionary<string, object> { { "subnetConfigs", subnetConfigsList } });
                    WriteJsonEntry(zip, "hardware/io_systems.json", new Dictionary<string, object> { { "ioSystems", ioSystemsList } });
                    WriteJsonEntry(zip, "hardware/topology.json", new Dictionary<string, object> { { "topology", topologyList } });
                }

                string downloadName = "export_" + project.Name + "_devices.zip";
                SendFile(ctx.Response, zipPath, downloadName);

                Console.WriteLine("Device config exported: " + devicesList.Count + " devices, " +
                    subnetsList.Count + " subnets, " + subnetConfigsList.Count + " subnet configs, " +
                    ioSystemsList.Count + " io systems, " + topologyList.Count + " topologies");
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Device export failed: " + ex.Message, stackTrace = ex.StackTrace });
            }
        }

        static void WriteJsonEntry(ZipArchive zip, string entryName, object data)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var stream = entry.Open())
            {
                byte[] buf = Encoding.UTF8.GetBytes(json.Serialize(data));
                stream.Write(buf, 0, buf.Length);
            }
        }

        static List<Dictionary<string, object>> CollectPluggedModules(Device device)
        {
            var slots = new List<Dictionary<string, object>>();
            foreach (DeviceItem di in device.DeviceItems)
            {
                CollectPluggedModulesRecursive(di, slots);
            }
            return slots;
        }

        static void CollectPluggedModulesRecursive(DeviceItem item, List<Dictionary<string, object>> slots)
        {
            // A plugged module has a PositionNumber > 0 and a TypeIdentifier
            // Skip the main rack/head module (position 0 or classification == Rack)
            if (item.Classification.ToString() == "PluggedModule" && item.PositionNumber > 0)
            {
                slots.Add(new Dictionary<string, object>
                {
                    { "slotNumber", item.PositionNumber },
                    { "typeIdentifier", item.TypeIdentifier ?? "" },
                    { "moduleName", item.Name ?? "" }
                });
            }

            foreach (DeviceItem child in item.DeviceItems)
            {
                CollectPluggedModulesRecursive(child, slots);
            }
        }

        static List<Tuple<NetworkInterface, DeviceItem>> FindAllNetworkInterfaces(Device device)
        {
            var interfaces = new List<Tuple<NetworkInterface, DeviceItem>>();
            foreach (DeviceItem di in device.DeviceItems)
            {
                FindNetworkInterfacesRecursive(di, interfaces);
            }
            return interfaces;
        }

        static void FindNetworkInterfacesRecursive(DeviceItem item, List<Tuple<NetworkInterface, DeviceItem>> interfaces)
        {
            try
            {
                var netIf = ((IEngineeringServiceProvider)item).GetService<NetworkInterface>();
                if (netIf != null)
                {
                    interfaces.Add(Tuple.Create(netIf, item));
                }
            }
            catch { }

            foreach (DeviceItem child in item.DeviceItems)
            {
                FindNetworkInterfacesRecursive(child, interfaces);
            }
        }

        static List<Tuple<NetworkPort, int>> FindNetworkPorts(DeviceItem interfaceItem)
        {
            var ports = new List<Tuple<NetworkPort, int>>();
            int portNum = 0;
            foreach (DeviceItem child in interfaceItem.DeviceItems)
            {
                FindNetworkPortsRecursive(child, ports, ref portNum);
            }
            return ports;
        }

        static void FindNetworkPortsRecursive(DeviceItem item, List<Tuple<NetworkPort, int>> ports, ref int portNum)
        {
            try
            {
                var netPort = ((IEngineeringServiceProvider)item).GetService<NetworkPort>();
                if (netPort != null)
                {
                    portNum++;
                    ports.Add(Tuple.Create(netPort, portNum));
                }
            }
            catch { }

            foreach (DeviceItem child in item.DeviceItems)
            {
                FindNetworkPortsRecursive(child, ports, ref portNum);
            }
        }

        static Device FindParentDevice(DeviceItem item)
        {
            // Walk up the hierarchy to find the parent Device
            IEngineeringObject current = item;
            while (current != null)
            {
                if (current is Device dev) return dev;
                try { current = current.Parent; } catch { return null; }
            }
            return null;
        }

        // 프로젝트 내 모든 Device 열거 (top-level + UngroupedDevicesGroup + DeviceGroups 재귀).
        // PROFINET IO 슬레이브(ET200 등 분산 IO 디바이스)는 보통 UngroupedDevicesGroup 에 있음.
        static List<Device> EnumerateAllDevices(Project proj)
        {
            var result = new List<Device>();
            var seen = new HashSet<string>();
            if (proj == null) return result;

            // Top-level
            try
            {
                foreach (Device d in proj.Devices)
                    if (d != null && d.Name != null && seen.Add(d.Name)) result.Add(d);
            }
            catch { }

            // Ungrouped devices (분산 IO 슬레이브)
            try
            {
                var ug = proj.GetType().GetProperty("UngroupedDevicesGroup")?.GetValue(proj, null);
                if (ug != null) CollectFromDeviceGroup(ug, result, seen);
            }
            catch { }

            // User-defined device groups (재귀)
            try
            {
                var gs = proj.GetType().GetProperty("DeviceGroups")?.GetValue(proj, null) as System.Collections.IEnumerable;
                if (gs != null) foreach (var g in gs) CollectFromDeviceGroup(g, result, seen);
            }
            catch { }

            return result;
        }

        static void CollectFromDeviceGroup(object group, List<Device> result, HashSet<string> seen)
        {
            if (group == null) return;
            try
            {
                var ds = group.GetType().GetProperty("Devices")?.GetValue(group, null) as System.Collections.IEnumerable;
                if (ds != null)
                    foreach (Device d in ds)
                        if (d != null && d.Name != null && seen.Add(d.Name)) result.Add(d);
            }
            catch { }
            try
            {
                var subs = group.GetType().GetProperty("Groups")?.GetValue(group, null) as System.Collections.IEnumerable;
                if (subs != null)
                    foreach (var sub in subs) CollectFromDeviceGroup(sub, result, seen);
            }
            catch { }
        }

        static Tuple<int, int> GetPortPosition(Device device, object targetPort)
        {
            int interfaceNum = 0;
            foreach (DeviceItem di in device.DeviceItems)
            {
                var result = GetPortPositionRecursive(di, targetPort, ref interfaceNum);
                if (result != null) return result;
            }
            return Tuple.Create(1, 1); // fallback
        }

        static Tuple<int, int> GetPortPositionRecursive(DeviceItem item, object targetPort, ref int interfaceNum)
        {
            try
            {
                var netIf = ((IEngineeringServiceProvider)item).GetService<NetworkInterface>();
                if (netIf != null) interfaceNum++;
            }
            catch { }

            try
            {
                var netPort = ((IEngineeringServiceProvider)item).GetService<NetworkPort>();
                if (netPort != null && ReferenceEquals(netPort, targetPort))
                {
                    int portNum = 1;
                    try { var numProp = netPort.GetType().GetProperty("Number"); if (numProp != null) portNum = (int)numProp.GetValue(netPort); } catch { }
                    return Tuple.Create(interfaceNum, portNum);
                }
            }
            catch { }

            foreach (DeviceItem child in item.DeviceItems)
            {
                var result = GetPortPositionRecursive(child, targetPort, ref interfaceNum);
                if (result != null) return result;
            }
            return null;
        }

        // ── Drive Configuration ──

        static DriveObjectContainer GetDriveContainer(string deviceName, string projectName = null)
        {
            foreach (Project proj in connectedPortal.Projects)
            {
                if (projectName != null && proj.Name != projectName) continue;
                foreach (Device dev in proj.Devices)
                {
                    if (dev.Name != deviceName) continue;
                    foreach (DeviceItem di in dev.DeviceItems)
                    {
                        var container = di.GetService<DriveObjectContainer>();
                        if (container != null) return container;
                        // Search one level deeper
                        foreach (DeviceItem child in di.DeviceItems)
                        {
                            container = child.GetService<DriveObjectContainer>();
                            if (container != null) return container;
                        }
                    }
                }
            }
            return null;
        }

        static void GetDriveObjects(HttpListenerContext ctx)
        {
            string deviceName = ctx.Request.QueryString["device"];
            string projectName = ctx.Request.QueryString["project"];
            EnsureConnected();
            if (string.IsNullOrEmpty(deviceName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?device= parameter" });
                return;
            }

            var container = GetDriveContainer(deviceName, projectName);
            if (container == null)
            {
                SendJson(ctx.Response, 404, new { error = "No DriveObjectContainer found for device: " + deviceName });
                return;
            }

            var driveObjects = new List<object>();
            foreach (DriveObject dobj in container.DriveObjects)
            {
                var paramCount = 0;
                try { paramCount = dobj.Parameters.Count; } catch { }

                driveObjects.Add(new {
                    number = dobj.DriveObjectNumber,
                    parameterCount = paramCount
                });
            }

            SendJson(ctx.Response, 200, new { device = deviceName, driveObjectCount = driveObjects.Count, driveObjects });
        }

        static void GetDriveParameters(HttpListenerContext ctx)
        {
            string deviceName = ctx.Request.QueryString["device"];
            string projectName = ctx.Request.QueryString["project"];
            string doNumStr = ctx.Request.QueryString["do"];
            string pNumStr = ctx.Request.QueryString["param"];
            string rangeStr = ctx.Request.QueryString["range"];
            EnsureConnected();

            if (string.IsNullOrEmpty(deviceName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?device= parameter. Optional: ?do=<driveObjectNumber>&param=<paramNumber>&range=300-320" });
                return;
            }

            var container = GetDriveContainer(deviceName, projectName);
            if (container == null)
            {
                SendJson(ctx.Response, 404, new { error = "No DriveObjectContainer found for device: " + deviceName });
                return;
            }

            int doNum = 0;
            if (!string.IsNullOrEmpty(doNumStr)) int.TryParse(doNumStr, out doNum);

            DriveObject targetDo = null;
            foreach (DriveObject dobj in container.DriveObjects)
            {
                if (dobj.DriveObjectNumber == doNum) { targetDo = dobj; break; }
            }
            if (targetDo == null)
            {
                // Fallback: first drive object
                foreach (DriveObject dobj in container.DriveObjects)
                {
                    targetDo = dobj;
                    break;
                }
            }
            if (targetDo == null)
            {
                SendJson(ctx.Response, 404, new { error = "No DriveObject found" });
                return;
            }

            // If specific param requested
            if (!string.IsNullOrEmpty(pNumStr))
            {
                int pNum;
                if (!int.TryParse(pNumStr, out pNum))
                {
                    SendJson(ctx.Response, 400, new { error = "Invalid param number" });
                    return;
                }
                var paramResult = ReadDriveParam(targetDo, pNum);
                SendJson(ctx.Response, 200, paramResult);
                return;
            }

            // If range requested (e.g. range=300-320)
            if (!string.IsNullOrEmpty(rangeStr))
            {
                var rangeParts = rangeStr.Split('-');
                int rangeStart, rangeEnd;
                if (rangeParts.Length == 2 && int.TryParse(rangeParts[0], out rangeStart) && int.TryParse(rangeParts[1], out rangeEnd))
                {
                    var rangeParams = new List<object>();
                    for (int i = rangeStart; i <= rangeEnd; i++)
                    {
                        var p = ReadDriveParam(targetDo, i);
                        if (p != null) rangeParams.Add(p);
                    }
                    SendJson(ctx.Response, 200, new { device = deviceName, driveObject = targetDo.DriveObjectNumber, range = rangeStr, count = rangeParams.Count, parameters = rangeParams });
                    return;
                }
            }

            // Default: return motor-related params (p300-p311)
            var motorParams = new List<object>();
            int[] defaultParams = { 300, 301, 302, 303, 304, 305, 306, 307, 308, 310, 311, 396, 397, 398 };
            foreach (int pn in defaultParams)
            {
                var p = ReadDriveParam(targetDo, pn);
                if (p != null) motorParams.Add(p);
            }

            SendJson(ctx.Response, 200, new { device = deviceName, driveObject = targetDo.DriveObjectNumber, count = motorParams.Count, parameters = motorParams });
        }

        static void DumpAllDriveParameters(HttpListenerContext ctx)
        {
            string deviceName = ctx.Request.QueryString["device"];
            string projectName = ctx.Request.QueryString["project"];
            string doNumStr = ctx.Request.QueryString["do"];
            string filterStr = ctx.Request.QueryString["filter"]; // "enum" to show only params with EnumValueList, "motor" for motor-related
            EnsureConnected();

            if (string.IsNullOrEmpty(deviceName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?device=. Optional: ?do=1&filter=enum (show only params with enum lists)" });
                return;
            }

            var container = GetDriveContainer(deviceName, projectName);
            if (container == null) { SendJson(ctx.Response, 404, new { error = "No DriveObjectContainer: " + deviceName }); return; }

            int doNum = -1;
            if (!string.IsNullOrEmpty(doNumStr)) int.TryParse(doNumStr, out doNum);

            DriveObject targetDo = null;
            foreach (DriveObject dobj in container.DriveObjects)
            {
                if (doNum >= 0 && dobj.DriveObjectNumber == doNum) { targetDo = dobj; break; }
                if (targetDo == null) targetDo = dobj; // fallback to first
            }
            if (targetDo == null) { SendJson(ctx.Response, 404, new { error = "No DriveObject found" }); return; }

            bool filterEnum = (filterStr == "enum");
            bool filterMotor = (filterStr == "motor");

            var allParams = new List<object>();
            foreach (DriveParameter dp in targetDo.Parameters)
            {
                try
                {
                    var enumValues = new Dictionary<string, string>();
                    try
                    {
                        if (dp.EnumValueList != null)
                            foreach (var kvp in dp.EnumValueList)
                                enumValues[kvp.Key.ToString()] = kvp.Value;
                    }
                    catch { }

                    if (filterEnum && enumValues.Count == 0) continue;

                    string nameText = dp.Name ?? "";
                    string paramText = dp.ParameterText ?? "";
                    if (filterMotor)
                    {
                        string combined = (nameText + " " + paramText).ToLower();
                        if (!combined.Contains("motor") && !combined.Contains("encoder") && !combined.Contains("brake") &&
                            !combined.Contains("mlfb") && !combined.Contains("order") && !combined.Contains("article") &&
                            !combined.Contains("code") && !combined.Contains("selection") && !combined.Contains("type"))
                            continue;
                    }

                    object value = null;
                    try { value = dp.Value; } catch { value = "<err>"; }

                    allParams.Add(new {
                        number = dp.Number,
                        name = dp.Name,
                        text = dp.ParameterText,
                        value,
                        unit = dp.Unit,
                        enumCount = enumValues.Count,
                        enumValues = enumValues.Count > 0 ? (object)enumValues : null
                    });
                }
                catch { }
            }

            SendJson(ctx.Response, 200, new {
                device = deviceName,
                driveObject = targetDo.DriveObjectNumber,
                filter = filterStr ?? "none",
                totalParameterCount = targetDo.Parameters.Count,
                matchedCount = allParams.Count,
                parameters = allParams
            });
        }

        static object ReadDriveParam(DriveObject dobj, int paramNumber)
        {
            try
            {
                foreach (DriveParameter dp in dobj.Parameters)
                {
                    if (dp.Number == paramNumber)
                    {
                        object value = null;
                        try { value = dp.Value; } catch { value = "<read error>"; }

                        var enumValues = new Dictionary<string, string>();
                        try
                        {
                            if (dp.EnumValueList != null)
                            {
                                foreach (var kvp in dp.EnumValueList)
                                    enumValues[kvp.Key.ToString()] = kvp.Value;
                            }
                        }
                        catch { }

                        return new {
                            number = dp.Number,
                            name = dp.Name,
                            text = dp.ParameterText,
                            value,
                            unit = dp.Unit,
                            min = dp.MinValue,
                            max = dp.MaxValue,
                            arrayIndex = dp.ArrayIndex,
                            arrayLength = dp.ArrayLength,
                            enumValues = enumValues.Count > 0 ? (object)enumValues : null
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                return new { number = paramNumber, error = ex.Message };
            }
            return null;
        }

        static void GetMotorConfiguration(HttpListenerContext ctx)
        {
            string deviceName = ctx.Request.QueryString["device"];
            string projectName = ctx.Request.QueryString["project"];
            string doNumStr = ctx.Request.QueryString["do"];
            string ddsStr = ctx.Request.QueryString["dds"];
            EnsureConnected();

            if (string.IsNullOrEmpty(deviceName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?device= parameter. Optional: ?do=<driveObjectNumber>&dds=<driveDataSetNumber>" });
                return;
            }

            var container = GetDriveContainer(deviceName, projectName);
            if (container == null)
            {
                SendJson(ctx.Response, 404, new { error = "No DriveObjectContainer found for device: " + deviceName });
                return;
            }

            int doNum = 0;
            if (!string.IsNullOrEmpty(doNumStr)) int.TryParse(doNumStr, out doNum);

            DriveObject targetDo = null;
            foreach (DriveObject dobj in container.DriveObjects)
            {
                if (dobj.DriveObjectNumber == doNum) { targetDo = dobj; break; }
            }
            if (targetDo == null)
            {
                foreach (DriveObject dobj in container.DriveObjects)
                { targetDo = dobj; break; }
            }
            if (targetDo == null)
            {
                SendJson(ctx.Response, 404, new { error = "No DriveObject found" });
                return;
            }

            ushort dds = 0;
            if (!string.IsNullOrEmpty(ddsStr)) ushort.TryParse(ddsStr, out dds);

            try
            {
                var dfi = targetDo.GetService<DriveFunctionInterface>();
                if (dfi == null)
                {
                    SendJson(ctx.Response, 404, new { error = "DriveFunctionInterface not available for this DriveObject" });
                    return;
                }

                // Debug: show what DFI properties are available
                var dfiInfo = new List<object>();
                try
                {
                    var dfiAttrs = ((IEngineeringObject)dfi).GetAttributeInfos();
                    foreach (var a in dfiAttrs)
                    {
                        string v = null;
                        try { var raw = ((IEngineeringObject)dfi).GetAttribute(a.Name); v = raw != null ? raw.ToString() : null; } catch { v = "<err>"; }
                        dfiInfo.Add(new { name = a.Name, value = v });
                    }
                }
                catch { }

                var dfiServices = new List<string>();
                try
                {
                    if (dfi.Commissioning != null) dfiServices.Add("Commissioning");
                } catch { }
                try
                {
                    if (dfi.DriveObjectFunctions != null) dfiServices.Add("DriveObjectFunctions");
                } catch { }
                try
                {
                    if (dfi.HardwareProjection != null) dfiServices.Add("HardwareProjection");
                } catch { }
                try
                {
                    if (dfi.FunctionInUse != null) dfiServices.Add("FunctionInUse");
                } catch { }
                try
                {
                    if (dfi.SafetyCommissioning != null) dfiServices.Add("SafetyCommissioning");
                } catch { }

                var hwProj = dfi.HardwareProjection;
                if (hwProj == null)
                {
                    SendJson(ctx.Response, 404, new {
                        error = "HardwareProjection not available",
                        driveObject = targetDo.DriveObjectNumber,
                        dfiAvailableServices = dfiServices,
                        dfiAttributes = dfiInfo
                    });
                    return;
                }

                var motorConfig = hwProj.GetCurrentMotorConfiguration(dds);
                if (motorConfig == null)
                {
                    SendJson(ctx.Response, 404, new { error = "No MotorConfiguration for DDS " + dds });
                    return;
                }

                var required = ReadConfigEntries(motorConfig.RequiredConfigurationEntries);
                var optional = ReadConfigEntries(motorConfig.OptionalConfigurationEntries);

                // Also get HardwareProjection attributes
                var hwAttrs = new List<object>();
                try
                {
                    var attrInfos = ((IEngineeringObject)hwProj).GetAttributeInfos();
                    foreach (var info in attrInfos)
                    {
                        string val = null;
                        try
                        {
                            var raw = ((IEngineeringObject)hwProj).GetAttribute(info.Name);
                            val = raw != null ? raw.ToString() : null;
                        }
                        catch { val = "<read error>"; }
                        hwAttrs.Add(new { name = info.Name, value = val });
                    }
                }
                catch { }

                SendJson(ctx.Response, 200, new {
                    device = deviceName,
                    driveObject = targetDo.DriveObjectNumber,
                    driveDataSet = dds,
                    requiredEntries = required,
                    optionalEntries = optional,
                    hwProjectionAttributes = hwAttrs
                });
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = ex.Message, type = ex.GetType().Name });
            }
        }

        static List<object> ReadConfigEntries(ConfigurationEntryComposition entries)
        {
            var result = new List<object>();
            if (entries == null) return result;
            foreach (ConfigurationEntry entry in entries)
            {
                var enumValues = new Dictionary<string, string>();
                try
                {
                    if (entry.EnumValueList != null)
                    {
                        foreach (var kvp in entry.EnumValueList)
                            enumValues[kvp.Key.ToString()] = kvp.Value;
                    }
                }
                catch { }

                result.Add(new {
                    name = entry.Name,
                    number = entry.Number,
                    description = entry.Description,
                    value = entry.Value,
                    unit = entry.Unit,
                    min = entry.MinValue,
                    max = entry.MaxValue,
                    enumValues = enumValues.Count > 0 ? (object)enumValues : null
                });
            }
            return result;
        }

        // ── Project Library ──

        static void GetLibraryList(HttpListenerContext ctx)
        {
            string projectName = ctx.Request.QueryString["project"];
            EnsureConnected();

            Project project = null;
            foreach (Project p in connectedPortal.Projects)
            {
                if (projectName == null || p.Name == projectName) { project = p; break; }
            }
            if (project == null) { SendJson(ctx.Response, 404, new { error = "No project found" }); return; }

            var lib = project.ProjectLibrary;
            if (lib == null) { SendJson(ctx.Response, 404, new { error = "ProjectLibrary not available" }); return; }

            var masterCopies = new List<object>();
            CollectMasterCopies(lib.MasterCopyFolder, "", masterCopies);

            var libraryTypes = new List<object>();
            CollectLibraryTypes(lib.TypeFolder, "", libraryTypes);

            SendJson(ctx.Response, 200, new {
                project = project.Name,
                masterCopyCount = masterCopies.Count,
                masterCopies,
                libraryTypeCount = libraryTypes.Count,
                libraryTypes
            });
        }

        static void CollectMasterCopies(MasterCopySystemFolder folder, string path, List<object> result)
        {
            foreach (MasterCopy mc in folder.MasterCopies)
            {
                var contents = new List<object>();
                foreach (MasterCopyContentDescription cd in mc.ContentDescriptions)
                {
                    contents.Add(new { contentName = cd.ContentName, contentType = cd.ContentType.Name });
                }
                result.Add(new { name = mc.Name, path, kind = "masterCopy", author = mc.Author, creationDate = mc.CreationDate.ToString("yyyy-MM-dd HH:mm:ss"), contents });
            }
            foreach (MasterCopyUserFolder sub in folder.Folders)
            {
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : path + "/" + sub.Name;
                CollectMasterCopiesUser(sub, subPath, result);
            }
        }

        static void CollectMasterCopiesUser(MasterCopyUserFolder folder, string path, List<object> result)
        {
            foreach (MasterCopy mc in folder.MasterCopies)
            {
                var contents = new List<object>();
                foreach (MasterCopyContentDescription cd in mc.ContentDescriptions)
                {
                    contents.Add(new { contentName = cd.ContentName, contentType = cd.ContentType.Name });
                }
                result.Add(new { name = mc.Name, path, kind = "masterCopy", author = mc.Author, creationDate = mc.CreationDate.ToString("yyyy-MM-dd HH:mm:ss"), contents });
            }
            foreach (MasterCopyUserFolder sub in folder.Folders)
            {
                string subPath = path + "/" + sub.Name;
                CollectMasterCopiesUser(sub, subPath, result);
            }
        }

        static void CollectLibraryTypes(LibraryTypeSystemFolder folder, string path, List<object> result)
        {
            foreach (LibraryType lt in folder.Types)
            {
                var versions = new List<object>();
                foreach (LibraryTypeVersion v in lt.Versions)
                {
                    versions.Add(new {
                        versionNumber = v.VersionNumber.ToString(),
                        state = v.State.ToString(),
                        author = v.Author
                    });
                }
                result.Add(new { name = lt.Name, path, kind = "libraryType", versionCount = versions.Count, versions });
            }
            foreach (LibraryTypeUserFolder sub in folder.Folders)
            {
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : path + "/" + sub.Name;
                CollectLibraryTypesUser(sub, subPath, result);
            }
        }

        static void CollectLibraryTypesUser(LibraryTypeUserFolder folder, string path, List<object> result)
        {
            foreach (LibraryType lt in folder.Types)
            {
                var versions = new List<object>();
                foreach (LibraryTypeVersion v in lt.Versions)
                {
                    versions.Add(new {
                        versionNumber = v.VersionNumber.ToString(),
                        state = v.State.ToString(),
                        author = v.Author
                    });
                }
                result.Add(new { name = lt.Name, path, kind = "libraryType", versionCount = versions.Count, versions });
            }
            foreach (LibraryTypeUserFolder sub in folder.Folders)
            {
                string subPath = path + "/" + sub.Name;
                CollectLibraryTypesUser(sub, subPath, result);
            }
        }

        static void ExportLibraryItem(HttpListenerContext ctx)
        {
            string projectName = ctx.Request.QueryString["project"];
            string itemName = ctx.Request.QueryString["name"];
            string itemPath = ctx.Request.QueryString["path"] ?? "";
            EnsureConnected();

            if (string.IsNullOrEmpty(itemName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?name= parameter. Use /api/library/list to see available items." });
                return;
            }

            Project project = null;
            foreach (Project p in connectedPortal.Projects)
            {
                if (projectName == null || p.Name == projectName) { project = p; break; }
            }
            if (project == null) { SendJson(ctx.Response, 404, new { error = "No project found" }); return; }

            var lib = project.ProjectLibrary;

            // Try as LibraryType first
            var lt = FindLibraryType(lib.TypeFolder, itemName, itemPath);
            if (lt != null)
            {
                LibraryTypeVersion latestVersion = null;
                foreach (LibraryTypeVersion v in lt.Versions) { latestVersion = v; }
                if (latestVersion == null) { SendJson(ctx.Response, 404, new { error = "No version found for type: " + itemName }); return; }

                ExportLibraryTypeAsZipResponse(ctx.Response, lt, latestVersion);
                return;
            }

            // MasterCopy는 직접 Export 불가
            var mc = FindMasterCopy(lib.MasterCopyFolder, itemName, itemPath);
            if (mc != null)
            {
                SendJson(ctx.Response, 400, new {
                    error = "MasterCopy는 직접 Export를 지원하지 않습니다. /api/library/copy-to-plc로 PLC에 복사한 후 /api/plc/export/xml을 사용하세요.",
                    name = mc.Name,
                    kind = "masterCopy",
                    hint = "GET /api/library/copy-to-plc?name=" + mc.Name + "&plc=PLC_1"
                });
                return;
            }

            SendJson(ctx.Response, 404, new { error = "Library item not found: " + itemName });
        }

        /// <summary>
        /// ExportAsDocuments로 내보낸 파일들을 ZIP으로 묶어서 응답.
        /// Import 시 ZIP을 풀어서 CreateFromDocuments로 사용 가능.
        /// </summary>
        static void ExportLibraryTypeAsZipResponse(HttpListenerResponse resp, LibraryType lt, LibraryTypeVersion version)
        {
            string exportDir = Path.Combine(tempDir, "libtype_" + Guid.NewGuid().ToString().Substring(0, 8));
            Directory.CreateDirectory(exportDir);
            string zipFile = Path.Combine(tempDir, MakeValidFileName(lt.Name) + "_libtype.zip");
            if (File.Exists(zipFile)) File.Delete(zipFile);

            try
            {
                string format = GetLibraryTypeExportFormat(lt);
                string fileNameNoExt = MakeValidFileName(lt.Name);
                bool usedDocuments = false;

                if (format != null)
                {
                    try
                    {
                        // Documents 포맷 지원 → ExportAsDocuments → ZIP
                        var result = version.ExportAsDocuments(new DirectoryInfo(exportDir), fileNameNoExt, format, LibraryExportOptions.None);
                        Console.WriteLine("  ExportAsDocuments: " + lt.Name + " format=" + format + " state=" + result.TransferResultState);
                        usedDocuments = true;
                    }
                    catch (Exception docEx)
                    {
                        // ExportAsDocuments 실패 (Faceplate 등) → Export() fallback
                        Console.WriteLine("  ExportAsDocuments failed for " + lt.Name + ": " + docEx.Message + " → falling back to Export()");
                        // exportDir 정리 후 재시도
                        foreach (string f in Directory.GetFiles(exportDir)) File.Delete(f);
                    }
                }

                if (usedDocuments)
                {
                    using (var zip = ZipFile.Open(zipFile, ZipArchiveMode.Create))
                    {
                        var metaEntry = zip.CreateEntry("_export_format.txt");
                        using (var sw = new StreamWriter(metaEntry.Open())) { sw.Write("documents"); }

                        foreach (string file in Directory.GetFiles(exportDir, "*", SearchOption.AllDirectories))
                        {
                            string entryName = file.Substring(exportDir.Length).TrimStart('\\', '/');
                            zip.CreateEntryFromFile(file, entryName);
                        }
                    }
                }
                else
                {
                    // Export() fallback → 단일 XML ZIP
                    string xmlFile = Path.Combine(exportDir, fileNameNoExt + ".xml");
                    version.Export(new FileInfo(xmlFile), ExportOptions.WithDefaults);
                    Console.WriteLine("  Export (xml fallback): " + lt.Name);

                    using (var zip = ZipFile.Open(zipFile, ZipArchiveMode.Create))
                    {
                        var metaEntry = zip.CreateEntry("_export_format.txt");
                        using (var sw = new StreamWriter(metaEntry.Open())) { sw.Write("xml"); }

                        zip.CreateEntryFromFile(xmlFile, fileNameNoExt + ".xml");
                    }
                }

                SendFile(resp, zipFile, fileNameNoExt + "_libtype.zip");
            }
            catch (Exception ex)
            {
                SendJson(resp, 500, new { error = "Library type export failed: " + ex.Message, type = ex.GetType().Name });
            }
            finally
            {
                if (File.Exists(zipFile)) File.Delete(zipFile);
                if (Directory.Exists(exportDir)) Directory.Delete(exportDir, true);
            }
        }

        static string GetLibraryTypeExportFormat(LibraryType lt)
        {
            try
            {
                var formats = lt.GetSupportedExportFormats();
                foreach (string f in formats)
                {
                    if (!string.IsNullOrEmpty(f)) return f;
                }
            }
            catch { }
            return null; // null = ExportAsDocuments 미지원 → Export() fallback
        }

        static void ExportLibraryZip(HttpListenerContext ctx)
        {
            string projectName = ctx.Request.QueryString["project"];
            EnsureConnected();

            Project project = null;
            foreach (Project p in connectedPortal.Projects)
            {
                if (projectName == null || p.Name == projectName) { project = p; break; }
            }
            if (project == null) { SendJson(ctx.Response, 404, new { error = "No project found" }); return; }

            var lib = project.ProjectLibrary;
            string exportDir = Path.Combine(tempDir, "lib_export_" + Guid.NewGuid().ToString().Substring(0, 8));
            Directory.CreateDirectory(exportDir);
            string zipFile = Path.Combine(tempDir, "library_export.zip");
            if (File.Exists(zipFile)) File.Delete(zipFile);

            var results = new List<Dictionary<string, object>>();
            int success = 0, failed = 0;

            try
            {
                using (var zip = ZipFile.Open(zipFile, ZipArchiveMode.Create))
                {
                    ExportLibraryTypesToZip(lib.TypeFolder, "", "LibraryTypes/", zip, exportDir, results, ref success, ref failed);
                }

                Console.WriteLine("Library export: " + success + " ok, " + failed + " failed");
                SendFileWithExportReport(ctx.Response, zipFile, "library_" + project.Name + ".zip", success, failed);
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Library ZIP export failed: " + ex.Message });
            }
            finally
            {
                if (File.Exists(zipFile)) File.Delete(zipFile);
                if (Directory.Exists(exportDir)) Directory.Delete(exportDir, true);
            }
        }

        static void ExportLibraryTypesToZip(LibraryTypeSystemFolder folder, string path, string zipPrefix,
            ZipArchive zip, string exportDir, List<Dictionary<string, object>> results, ref int success, ref int failed)
        {
            foreach (LibraryType lt in folder.Types)
            {
                ExportSingleLibraryTypeToZip(lt, path, zipPrefix, zip, exportDir, results, ref success, ref failed);
            }
            foreach (LibraryTypeUserFolder sub in folder.Folders)
            {
                string subPath = string.IsNullOrEmpty(path) ? sub.Name : path + "/" + sub.Name;
                ExportLibraryTypesUserToZip(sub, subPath, zipPrefix, zip, exportDir, results, ref success, ref failed);
            }
        }

        static void ExportLibraryTypesUserToZip(LibraryTypeUserFolder folder, string path, string zipPrefix,
            ZipArchive zip, string exportDir, List<Dictionary<string, object>> results, ref int success, ref int failed)
        {
            foreach (LibraryType lt in folder.Types)
            {
                ExportSingleLibraryTypeToZip(lt, path, zipPrefix, zip, exportDir, results, ref success, ref failed);
            }
            foreach (LibraryTypeUserFolder sub in folder.Folders)
            {
                string subPath = path + "/" + sub.Name;
                ExportLibraryTypesUserToZip(sub, subPath, zipPrefix, zip, exportDir, results, ref success, ref failed);
            }
        }

        static void ExportSingleLibraryTypeToZip(LibraryType lt, string path, string zipPrefix,
            ZipArchive zip, string exportDir, List<Dictionary<string, object>> results, ref int success, ref int failed)
        {
            LibraryTypeVersion latestVersion = null;
            foreach (LibraryTypeVersion v in lt.Versions) { latestVersion = v; }
            if (latestVersion == null) return;

            // ExportAsDocuments는 디렉토리에 여러 파일을 생성 → 서브 디렉토리로 분리
            string typeName = MakeValidFileName(lt.Name);
            string typeExportDir = Path.Combine(exportDir, typeName);
            if (Directory.Exists(typeExportDir)) Directory.Delete(typeExportDir, true);
            Directory.CreateDirectory(typeExportDir);

            try
            {
                string format = GetLibraryTypeExportFormat(lt);
                string exportFormat = "xml";

                if (format != null)
                {
                    try
                    {
                        // Documents 포맷
                        var result = latestVersion.ExportAsDocuments(new DirectoryInfo(typeExportDir), typeName, format, LibraryExportOptions.None);
                        exportFormat = "documents";
                    }
                    catch (Exception docEx)
                    {
                        // ExportAsDocuments 실패 (Faceplate 등) → Export() fallback
                        Console.WriteLine("  ExportAsDocuments failed for " + lt.Name + ": " + docEx.Message + " → falling back to Export()");
                        foreach (string f in Directory.GetFiles(typeExportDir)) File.Delete(f);
                    }
                }

                if (exportFormat != "documents")
                {
                    // Fallback: Export() → 단일 XML
                    string xmlFile = Path.Combine(typeExportDir, typeName + ".xml");
                    latestVersion.Export(new FileInfo(xmlFile), ExportOptions.WithDefaults);
                    exportFormat = "xml";
                }

                // ZIP에 export된 모든 파일 추가
                string basePath = string.IsNullOrEmpty(path)
                    ? zipPrefix + typeName + "/"
                    : zipPrefix + path + "/" + typeName + "/";

                // _export_format 메타 추가
                var metaEntry = zip.CreateEntry(basePath + "_export_format.txt");
                using (var sw = new StreamWriter(metaEntry.Open())) { sw.Write(exportFormat); }

                foreach (string file in Directory.GetFiles(typeExportDir, "*", SearchOption.AllDirectories))
                {
                    string entryName = basePath + file.Substring(typeExportDir.Length).TrimStart('\\', '/');
                    zip.CreateEntryFromFile(file, entryName);
                }

                Console.WriteLine("  Library Type (" + exportFormat + "): " + basePath);
                results.Add(new Dictionary<string, object> { {"file", typeName}, {"path", path}, {"kind", "libraryType"}, {"status", "ok"}, {"format", exportFormat} });
                success++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Library Type error [" + lt.Name + "]: " + ex.Message);
                results.Add(new Dictionary<string, object> { {"file", typeName}, {"path", path}, {"kind", "libraryType"}, {"status", "error"}, {"error", ex.Message} });
                failed++;
            }
            finally
            {
                if (Directory.Exists(typeExportDir)) Directory.Delete(typeExportDir, true);
            }
        }

        static void ImportLibraryItem(HttpListenerContext ctx)
        {
            string projectName = ctx.Request.QueryString["project"];
            string plcName = ctx.Request.QueryString["plc"];
            string targetKind = (ctx.Request.QueryString["kind"] ?? "masterCopy").ToLower();
            string targetPath = ctx.Request.QueryString["path"] ?? "";
            EnsureConnected();

            Project project = null;
            foreach (Project p in connectedPortal.Projects)
            {
                if (projectName == null || p.Name == projectName) { project = p; break; }
            }
            if (project == null) { SendJson(ctx.Response, 404, new { error = "No project found" }); return; }

            var lib = project.ProjectLibrary;

            // Read uploaded XML body
            byte[] body;
            using (var ms = new MemoryStream())
            {
                ctx.Request.InputStream.CopyTo(ms);
                body = ms.ToArray();
            }
            if (body.Length == 0)
            {
                SendJson(ctx.Response, 400, new { error = "Empty request body. POST the XML file content." });
                return;
            }

            string uploadFile = Path.Combine(tempDir, "lib_import_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".xml");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(uploadFile, body);

            string shortPath = CopyToFlat(uploadFile, @"C:\T\libimport");
            File.Delete(uploadFile);

            try
            {
                if (targetKind == "mastercopy")
                {
                    // MasterCopy: XML → PLC block import → Create MasterCopy from block
                    if (string.IsNullOrEmpty(plcName))
                    {
                        SendJson(ctx.Response, 400, new { error = "MasterCopy import에는 ?plc= 파라미터가 필요합니다. XML을 PLC에 import 후 MasterCopy를 생성합니다." });
                        return;
                    }

                    var plc = FindPlc(plcName);
                    if (plc == null) { SendJson(ctx.Response, 404, new { error = "PLC not found: " + plcName }); return; }

                    // Import XML to PLC (temp)
                    plc.BlockGroup.Blocks.Import(new FileInfo(shortPath), ImportOptions.Override);

                    // Find the imported block by parsing XML for its name
                    string blockName = ExtractBlockNameFromXml(shortPath);
                    if (string.IsNullOrEmpty(blockName))
                    {
                        SendJson(ctx.Response, 200, new {
                            message = "XML이 PLC에 import 되었지만, 블록 이름을 추출할 수 없어 MasterCopy 자동 생성을 건너뛰었습니다. /api/library/create-mastercopy로 수동 생성하세요.",
                            kind = "masterCopy", step = "partial"
                        });
                        return;
                    }

                    var block = FindBlock(plc.BlockGroup, blockName);
                    if (block == null)
                    {
                        SendJson(ctx.Response, 200, new {
                            message = "XML이 PLC에 import 되었습니다. 블록을 찾지 못해 MasterCopy 자동 생성을 건너뛰었습니다.",
                            blockName, kind = "masterCopy", step = "partial"
                        });
                        return;
                    }

                    // Create MasterCopy from the block
                    MasterCopyComposition mcTarget;
                    if (string.IsNullOrEmpty(targetPath))
                    {
                        mcTarget = lib.MasterCopyFolder.MasterCopies;
                    }
                    else
                    {
                        var folder = NavigateOrCreateMasterCopyFolder(lib.MasterCopyFolder, targetPath);
                        if (folder == null) { SendJson(ctx.Response, 500, new { error = "Failed to create folder: " + targetPath }); return; }
                        mcTarget = folder.MasterCopies;
                    }

                    var newMc = mcTarget.Create((IMasterCopySource)block);
                    SendJson(ctx.Response, 200, new { message = "MasterCopy created from imported block", name = newMc.Name, kind = "masterCopy", path = targetPath });
                }
                else
                {
                    // LibraryType import
                    string docDir = null;
                    bool isZip = body.Length >= 4 && body[0] == 0x50 && body[1] == 0x4B && body[2] == 0x03 && body[3] == 0x04;

                    LibraryTypeComposition typeTarget;
                    if (string.IsNullOrEmpty(targetPath))
                    {
                        typeTarget = lib.TypeFolder.Types;
                    }
                    else
                    {
                        var folder = NavigateOrCreateLibraryTypeFolder(lib.TypeFolder, targetPath);
                        if (folder == null) { SendJson(ctx.Response, 500, new { error = "Failed to create folder: " + targetPath }); return; }
                        typeTarget = folder.Types;
                    }

                    try
                    {
                        if (isZip)
                        {
                            docDir = Path.Combine(tempDir, "libtype_import_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                            Directory.CreateDirectory(docDir);
                            ZipFile.ExtractToDirectory(shortPath, docDir);

                            // _export_format.txt 확인하여 documents vs xml 분기
                            string formatFile = Path.Combine(docDir, "_export_format.txt");
                            string exportFormat = File.Exists(formatFile) ? File.ReadAllText(formatFile).Trim() : "documents";

                            if (exportFormat == "xml")
                            {
                                // Export()로 만든 XML → PLC import처럼 직접은 불가, 안내 반환
                                // 하지만 시도: 해당 XML을 찾아서 CreateFromDocuments 시도
                                var xmlFiles = Directory.GetFiles(docDir, "*.xml", SearchOption.AllDirectories);
                                if (xmlFiles.Length == 0) { SendJson(ctx.Response, 400, new { error = "ZIP에 XML 파일이 없습니다" }); return; }

                                SendJson(ctx.Response, 400, new {
                                    error = "이 타입은 Documents 포맷 export를 지원하지 않아 Export() fallback으로 내보내졌습니다. CreateFromDocuments로 import할 수 없습니다. PLC에 직접 import 후 라이브러리에 추가하세요.",
                                    hint = "1) POST /api/plc/import/upload?plc=PLC_1 로 XML import → 2) GET /api/library/create-mastercopy?plc=PLC_1&block=블록명"
                                });
                                return;
                            }

                            // Documents 포맷: CreateFromDocuments
                            var xmlDocs = Directory.GetFiles(docDir, "*.xml", SearchOption.AllDirectories);
                            if (xmlDocs.Length == 0) { SendJson(ctx.Response, 400, new { error = "ZIP에 XML 파일이 없습니다" }); return; }
                            string fileNameNoExt = Path.GetFileNameWithoutExtension(xmlDocs[0]);

                            var result = typeTarget.CreateFromDocuments(new DirectoryInfo(docDir), fileNameNoExt, LibraryImportOptions.None);
                            SendJson(ctx.Response, 200, new {
                                message = "Library type imported",
                                kind = "libraryType",
                                path = targetPath,
                                state = result.TransferResultState.ToString()
                            });
                        }
                        else
                        {
                            // 단일 파일 → 디렉토리 기반 CreateFromDocuments 시도
                            string dirPath = Path.GetDirectoryName(shortPath);
                            string fileNameNoExt = Path.GetFileNameWithoutExtension(shortPath);

                            var result = typeTarget.CreateFromDocuments(new DirectoryInfo(dirPath), fileNameNoExt, LibraryImportOptions.None);
                            SendJson(ctx.Response, 200, new {
                                message = "Library type imported",
                                kind = "libraryType",
                                path = targetPath,
                                state = result.TransferResultState.ToString()
                            });
                        }
                    }
                    finally
                    {
                        if (docDir != null && Directory.Exists(docDir))
                        {
                            try { Directory.Delete(docDir, true); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Library import failed: " + ex.Message, type = ex.GetType().Name });
            }
            finally
            {
                if (File.Exists(shortPath)) File.Delete(shortPath);
            }
        }

        static string ExtractBlockNameFromXml(string xmlPath)
        {
            try
            {
                var doc = XDocument.Load(xmlPath);
                // Try common TIA XML patterns: <SW.Blocks.FB Name="..."> etc.
                var swBlock = doc.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName.StartsWith("SW.Blocks.") ||
                    e.Name.LocalName.StartsWith("SW.Types.") ||
                    e.Name.LocalName.StartsWith("SW.Tags."));
                if (swBlock != null)
                {
                    var nameAttr = swBlock.Attribute("Name");
                    if (nameAttr != null) return nameAttr.Value;
                }
                // Fallback: look for <AttributeList><Name>...</Name>
                var nameEl = doc.Descendants().Where(e => e.Name.LocalName == "Name" && e.Parent != null && e.Parent.Name.LocalName == "AttributeList").FirstOrDefault();
                if (nameEl != null) return nameEl.Value;
            }
            catch { }
            return null;
        }

        static void CopyLibraryToPlc(HttpListenerContext ctx)
        {
            string projectName = ctx.Request.QueryString["project"];
            string itemName = ctx.Request.QueryString["name"];
            string itemPath = ctx.Request.QueryString["path"] ?? "";
            string plcName = ctx.Request.QueryString["plc"];
            string group = ctx.Request.QueryString["group"] ?? "";
            EnsureConnected();

            if (string.IsNullOrEmpty(itemName) || string.IsNullOrEmpty(plcName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?name= and ?plc= parameters" });
                return;
            }

            Project project = null;
            foreach (Project p in connectedPortal.Projects)
            {
                if (projectName == null || p.Name == projectName) { project = p; break; }
            }
            if (project == null) { SendJson(ctx.Response, 404, new { error = "No project found" }); return; }

            var plc = FindPlc(plcName);
            if (plc == null) { SendJson(ctx.Response, 404, new { error = "PLC not found: " + plcName }); return; }

            var lib = project.ProjectLibrary;

            try
            {
                var mc = FindMasterCopy(lib.MasterCopyFolder, itemName, itemPath);
                if (mc != null)
                {
                    // MasterCopy → PLC: PlcBlockGroup implements IMasterCopyTarget
                    PlcBlockGroup targetGroup = plc.BlockGroup;
                    if (!string.IsNullOrEmpty(group))
                    {
                        targetGroup = NavigateOrCreateBlockGroup(plc.BlockGroup, group);
                    }
                    targetGroup.Blocks.CreateFrom(mc);
                    SendJson(ctx.Response, 200, new { message = "MasterCopy copied to PLC", name = mc.Name, plc = plcName, group });
                    return;
                }

                // Try LibraryType
                var lt = FindLibraryType(lib.TypeFolder, itemName, itemPath);
                if (lt != null)
                {
                    LibraryTypeVersion latestVersion = null;
                    foreach (LibraryTypeVersion v in lt.Versions) { latestVersion = v; }
                    if (latestVersion == null) { SendJson(ctx.Response, 404, new { error = "No version for type: " + itemName }); return; }

                    // LibraryType → PLC: export XML, then import to PLC
                    string tempExport = Path.Combine(tempDir, MakeValidFileName(itemName) + ".xml");
                    if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                    if (File.Exists(tempExport)) File.Delete(tempExport);

                    latestVersion.Export(new FileInfo(tempExport), ExportOptions.WithDefaults);
                    string shortPath = CopyToFlat(tempExport, @"C:\T\libcopy");
                    File.Delete(tempExport);

                    try
                    {
                        plc.TypeGroup.Types.Import(new FileInfo(shortPath), ImportOptions.Override);
                        SendJson(ctx.Response, 200, new { message = "LibraryType copied to PLC", name = lt.Name, plc = plcName });
                    }
                    finally { if (File.Exists(shortPath)) File.Delete(shortPath); }
                    return;
                }

                SendJson(ctx.Response, 404, new { error = "Library item not found: " + itemName });
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Copy to PLC failed: " + ex.Message, type = ex.GetType().Name });
            }
        }

        static void CreateMasterCopyFromBlock(HttpListenerContext ctx)
        {
            string projectName = ctx.Request.QueryString["project"];
            string plcName = ctx.Request.QueryString["plc"];
            string blockName = ctx.Request.QueryString["block"];
            string targetPath = ctx.Request.QueryString["path"] ?? "";
            EnsureConnected();

            if (string.IsNullOrEmpty(plcName) || string.IsNullOrEmpty(blockName))
            {
                SendJson(ctx.Response, 400, new { error = "Missing ?plc= and ?block= parameters" });
                return;
            }

            Project project = null;
            foreach (Project p in connectedPortal.Projects)
            {
                if (projectName == null || p.Name == projectName) { project = p; break; }
            }
            if (project == null) { SendJson(ctx.Response, 404, new { error = "No project found" }); return; }

            var plc = FindPlc(plcName);
            if (plc == null) { SendJson(ctx.Response, 404, new { error = "PLC not found: " + plcName }); return; }

            var block = FindBlock(plc.BlockGroup, blockName);
            if (block == null) { SendJson(ctx.Response, 404, new { error = "Block not found: " + blockName }); return; }

            var lib = project.ProjectLibrary;

            try
            {
                MasterCopyComposition mcTarget;
                if (string.IsNullOrEmpty(targetPath))
                {
                    mcTarget = lib.MasterCopyFolder.MasterCopies;
                }
                else
                {
                    var folder = NavigateOrCreateMasterCopyFolder(lib.MasterCopyFolder, targetPath);
                    if (folder == null) { SendJson(ctx.Response, 500, new { error = "Failed to create folder: " + targetPath }); return; }
                    mcTarget = folder.MasterCopies;
                }

                var newMc = mcTarget.Create((IMasterCopySource)block);
                SendJson(ctx.Response, 200, new { message = "MasterCopy created", name = newMc.Name, sourcePLC = plcName, sourceBlock = blockName, path = targetPath });
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "MasterCopy creation failed: " + ex.Message, type = ex.GetType().Name });
            }
        }

        // ── Global Library ──

        /// <summary>
        /// API 1: .al20 파일 업로드 → Global Library로 열기 (메모리에 유지)
        /// </summary>
        static void ImportGlobalLibraryToProject(HttpListenerContext ctx)
        {
            EnsureConnected();

            // filename 쿼리 파라미터에서 .al20/.zip 확장자를 제거하여 라이브러리 이름으로 사용
            // 예: ?filename=Library1.al20.zip → "Library1"
            // 하위 호환: name 파라미터도 지원
            string libName = ctx.Request.QueryString["name"];
            string fileName = ctx.Request.QueryString["filename"];
            if (string.IsNullOrEmpty(libName) && !string.IsNullOrEmpty(fileName))
            {
                string n = Path.GetFileNameWithoutExtension(fileName); // .zip 제거
                if (n.EndsWith(".al20", StringComparison.OrdinalIgnoreCase))
                    n = n.Substring(0, n.Length - 5); // .al20 제거
                libName = n;
            }

            // ZIP 업로드 받기 (.al20 폴더를 ZIP으로 압축한 것)
            byte[] body;
            using (var ms = new MemoryStream())
            {
                ctx.Request.InputStream.CopyTo(ms);
                body = ms.ToArray();
            }
            if (body.Length == 0)
            {
                SendJson(ctx.Response, 400, new { error = "파일이 비어있습니다. .al20 폴더를 ZIP으로 압축하여 POST body로 업로드하세요." });
                return;
            }

            // .al20은 폴더 구조이므로 ZIP을 풀어서 복원
            if (string.IsNullOrEmpty(libName)) libName = "uploaded_library";
            string registeredName = MakeValidFileName(libName);

            string glibBaseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "global_libraries");
            if (!Directory.Exists(glibBaseDir)) Directory.CreateDirectory(glibBaseDir);

            // ZIP을 임시 파일로 저장 후 압축 해제
            string zipPath = Path.Combine(glibBaseDir, registeredName + ".zip");
            File.WriteAllBytes(zipPath, body);

            string extractDir = Path.Combine(glibBaseDir, registeredName + "_extract");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);

            try
            {
                ZipFile.ExtractToDirectory(zipPath, extractDir);
            }
            catch (Exception ex)
            {
                if (File.Exists(zipPath)) try { File.Delete(zipPath); } catch { }
                if (Directory.Exists(extractDir)) try { Directory.Delete(extractDir, true); } catch { }
                SendJson(ctx.Response, 500, new { error = "ZIP 압축 해제 실패: " + ex.Message, type = ex.GetType().Name });
                return;
            }
            finally
            {
                if (File.Exists(zipPath)) try { File.Delete(zipPath); } catch { }
            }

            // 압축 해제된 폴더에서 .al20 파일 찾기
            string al20File = null;
            foreach (string file in Directory.GetFiles(extractDir, "*.al20", SearchOption.AllDirectories))
            {
                al20File = file;
                break;
            }
            if (al20File == null)
            {
                if (Directory.Exists(extractDir)) try { Directory.Delete(extractDir, true); } catch { }
                SendJson(ctx.Response, 400, new { error = "ZIP 안에 .al20 파일을 찾을 수 없습니다." });
                return;
            }

            // filename 파라미터 없이 호출된 경우 .al20 파일명에서 이름 추출
            if (libName == "uploaded_library")
            {
                string detected = Path.GetFileNameWithoutExtension(al20File);
                if (!string.IsNullOrEmpty(detected))
                {
                    registeredName = MakeValidFileName(detected);
                }
            }

            string libPath = al20File;
            Console.WriteLine("Global Library extracted: " + registeredName + " (" + body.Length + " bytes ZIP) → " + libPath);

            // 이미 같은 이름으로 열려있으면 먼저 닫기
            if (openedGlobalLibraries.ContainsKey(registeredName))
            {
                try { openedGlobalLibraries[registeredName].Close(); } catch { }
                openedGlobalLibraries.Remove(registeredName);
            }

            try
            {
                var globalLib = connectedPortal.GlobalLibraries.Open(new FileInfo(libPath), OpenMode.ReadOnly);
                openedGlobalLibraries[registeredName] = globalLib;

                // 내용물 조회
                var masterCopies = new List<object>();
                CollectMasterCopies(globalLib.MasterCopyFolder, "", masterCopies);
                var libraryTypes = new List<object>();
                CollectLibraryTypes(globalLib.TypeFolder, "", libraryTypes);

                SendJson(ctx.Response, 200, new {
                    message = "Global Library 열기 성공 — /api/global-library/to-project-library 로 프로젝트에 복사할 수 있습니다",
                    name = registeredName,
                    uploadSize = body.Length,
                    libraryPath = libPath,
                    masterCopyCount = masterCopies.Count,
                    masterCopies,
                    libraryTypeCount = libraryTypes.Count,
                    libraryTypes
                });
            }
            catch (Exception ex)
            {
                if (Directory.Exists(extractDir)) try { Directory.Delete(extractDir, true); } catch { }
                SendJson(ctx.Response, 500, new { error = "Global Library 열기 실패: " + ex.Message, type = ex.GetType().Name, libraryPath = libPath });
            }
        }

        /// <summary>
        /// libName 으로 Global Library 를 찾는다. 우선순위:
        ///   1) openedGlobalLibraries 캐시 (우리 API 로 import/open 한 것)
        ///   2) connectedPortal.GlobalLibraries (TIA Portal 에 직접 열린 것)
        /// libName 이 비어있고 후보가 1개뿐이면 자동 선택.
        /// 반환값: ILibrary (찾지 못하면 null), 동시에 시도된 후보 이름들도 채움.
        /// </summary>
        static ILibrary ResolveGlobalLibraryByName(string libName, out string source, out string resolvedName, out List<string> available)
        {
            source = null;
            resolvedName = libName;
            available = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) 캐시 우선
            foreach (var k in openedGlobalLibraries.Keys)
            {
                if (seen.Add(k)) available.Add(k);
            }
            if (!string.IsNullOrEmpty(libName) && openedGlobalLibraries.ContainsKey(libName))
            {
                source = "cache";
                resolvedName = libName;
                return openedGlobalLibraries[libName];
            }

            // 2) connectedPortal.GlobalLibraries
            var portalLibs = new List<KeyValuePair<string, ILibrary>>();
            if (connectedPortal != null)
            {
                System.Collections.IEnumerable glColl = null;
                try
                {
                    var glProp = connectedPortal.GetType().GetProperty("GlobalLibraries");
                    if (glProp != null) glColl = glProp.GetValue(connectedPortal) as System.Collections.IEnumerable;
                }
                catch { }
                if (glColl != null)
                {
                    foreach (var gl in glColl)
                    {
                        string n = null;
                        try { n = ((IEngineeringObject)gl).GetAttribute("Name") as string; } catch { }
                        if (string.IsNullOrEmpty(n))
                        {
                            try { n = gl.GetType().GetProperty("Name")?.GetValue(gl) as string; } catch { }
                        }
                        if (string.IsNullOrEmpty(n)) continue;
                        var asLib = gl as ILibrary;
                        if (asLib == null) continue;
                        if (seen.Add(n)) available.Add(n);
                        portalLibs.Add(new KeyValuePair<string, ILibrary>(n, asLib));
                    }
                }
            }

            // 이름 매칭
            if (!string.IsNullOrEmpty(libName))
            {
                foreach (var kvp in portalLibs)
                {
                    if (string.Equals(kvp.Key, libName, StringComparison.OrdinalIgnoreCase))
                    {
                        source = "portal";
                        resolvedName = kvp.Key;
                        return kvp.Value;
                    }
                }
                return null;
            }

            // libName 미지정 → 후보 1개이면 자동 선택
            if (available.Count == 1)
            {
                if (openedGlobalLibraries.Count == 1)
                {
                    foreach (var kv in openedGlobalLibraries)
                    {
                        source = "cache";
                        resolvedName = kv.Key;
                        return kv.Value;
                    }
                }
                if (portalLibs.Count == 1)
                {
                    source = "portal";
                    resolvedName = portalLibs[0].Key;
                    return portalLibs[0].Value;
                }
            }
            return null;
        }

        /// <summary>
        /// 네이티브 LibraryType.UpdateLibrary / ILibrary.UpdateLibrary 사용한 Global → Project Library 복사.
        /// 기존 GlobalLibraryToProjectLibrary 의 export/import 우회로와 달리 in-process 직접 복사.
        /// type 파라미터를 주면 단일 타입, 생략하면 전체(모든 types + 모든 user folders) 일괄 처리.
        /// 의미: 'Update' — 타깃에 같은 GUID 타입이 있으면 새 version 추가, 없으면 새로 생성.
        /// </summary>
        static void UpdateProjectLibraryFromGlobal(HttpListenerContext ctx)
        {
            string libName = ctx.Request.QueryString["lib"];
            string projectName = ctx.Request.QueryString["project"];
            string typeName = ctx.Request.QueryString["type"];
            string typePath = ctx.Request.QueryString["path"] ?? "";
            string forceUpdateStr = ctx.Request.QueryString["forceUpdate"];
            string deleteUnusedStr = ctx.Request.QueryString["deleteUnused"];
            string structureStr = ctx.Request.QueryString["structure"];
            EnsureConnected();

            // 우리 캐시(openedGlobalLibraries) + connectedPortal.GlobalLibraries 모두에서 이름으로 검색
            string resolvedSource = null;
            ILibrary globalLib = ResolveGlobalLibraryByName(libName, out resolvedSource, out string resolvedName, out List<string> available);
            if (globalLib == null)
            {
                if (available.Count == 0)
                {
                    SendJson(ctx.Response, 404, new
                    {
                        error = "열려있는 Global Library가 없습니다. /api/global-library/import 또는 /api/global-library/open 으로 열어주세요. (TIA Portal 에서 직접 연 라이브러리도 인식 가능)"
                    });
                    return;
                }
                if (string.IsNullOrEmpty(libName))
                {
                    SendJson(ctx.Response, 400, new
                    {
                        error = "여러 Global Library 중 어느 것을 사용할지 ?lib= 으로 지정하세요",
                        available = available
                    });
                    return;
                }
                SendJson(ctx.Response, 404, new
                {
                    error = "Global Library를 찾을 수 없습니다: " + libName,
                    available = available
                });
                return;
            }
            libName = resolvedName;

            Project project = null;
            foreach (Project p in connectedPortal.Projects)
            {
                if (projectName == null || p.Name == projectName) { project = p; break; }
            }
            if (project == null) { SendJson(ctx.Response, 404, new { error = "프로젝트를 찾을 수 없습니다" }); return; }

            var projLib = project.ProjectLibrary;
            if (projLib == null) { SendJson(ctx.Response, 500, new { error = "ProjectLibrary 사용 불가" }); return; }

            // 옵션 enum 파싱 (셋 다 지정되면 4-arg overload, 아니면 단순 overload 사용)
            ForceUpdateMode forceUpdateMode = ForceUpdateMode.SetOnlyHigherUpdatedVersionAsDefault;
            DeleteUnusedVersionsMode deleteUnusedMode = DeleteUnusedVersionsMode.DoNotDelete;
            StructureConflictResolutionMode structureMode = StructureConflictResolutionMode.RetainStructure;
            bool useFullOverload = false;

            if (!string.IsNullOrEmpty(forceUpdateStr))
            {
                if (!Enum.TryParse<ForceUpdateMode>(forceUpdateStr, out forceUpdateMode))
                {
                    SendJson(ctx.Response, 400, new
                    {
                        error = "Invalid forceUpdate",
                        allowed = Enum.GetNames(typeof(ForceUpdateMode))
                    });
                    return;
                }
                useFullOverload = true;
            }
            if (!string.IsNullOrEmpty(deleteUnusedStr))
            {
                if (!Enum.TryParse<DeleteUnusedVersionsMode>(deleteUnusedStr, out deleteUnusedMode))
                {
                    SendJson(ctx.Response, 400, new
                    {
                        error = "Invalid deleteUnused",
                        allowed = Enum.GetNames(typeof(DeleteUnusedVersionsMode))
                    });
                    return;
                }
                useFullOverload = true;
            }
            if (!string.IsNullOrEmpty(structureStr))
            {
                if (!Enum.TryParse<StructureConflictResolutionMode>(structureStr, out structureMode))
                {
                    SendJson(ctx.Response, 400, new
                    {
                        error = "Invalid structure",
                        allowed = Enum.GetNames(typeof(StructureConflictResolutionMode))
                    });
                    return;
                }
                useFullOverload = true;
            }

            try
            {
                if (!string.IsNullOrEmpty(typeName))
                {
                    // ── 단일 타입: LibraryType.UpdateLibrary(ILibrary) ──
                    var lt = FindLibraryType(globalLib.TypeFolder, typeName, typePath);
                    if (lt == null)
                    {
                        SendJson(ctx.Response, 404, new { error = "LibraryType 찾을 수 없음: " + typeName, path = typePath });
                        return;
                    }

                    if (useFullOverload)
                        lt.UpdateLibrary(projLib, deleteUnusedMode, structureMode, forceUpdateMode);
                    else
                        lt.UpdateLibrary(projLib);

                    SendJson(ctx.Response, 200, new
                    {
                        message = "LibraryType 업데이트 완료 (네이티브 UpdateLibrary)",
                        sourceLibrary = libName,
                        targetProject = project.Name,
                        type = lt.Name,
                        path = typePath,
                        forceUpdate = useFullOverload ? forceUpdateMode.ToString() : "default(SetOnlyHigherUpdatedVersionAsDefault)",
                        deleteUnused = useFullOverload ? deleteUnusedMode.ToString() : "default(DoNotDelete)",
                        structure = useFullOverload ? structureMode.ToString() : "default(RetainStructure)"
                    });
                    return;
                }

                // ── 일괄: ILibrary.UpdateLibrary(selections, ILibrary) ──
                // LibraryType 과 LibraryTypeUserFolder 가 ILibraryTypeOrFolderSelection 을 구현
                var sels = new List<ILibraryTypeOrFolderSelection>();
                foreach (LibraryType lt in globalLib.TypeFolder.Types) sels.Add(lt);
                foreach (LibraryTypeUserFolder uf in globalLib.TypeFolder.Folders) sels.Add(uf);

                if (sels.Count == 0)
                {
                    SendJson(ctx.Response, 200, new
                    {
                        message = "Global Library에 LibraryType이 없습니다",
                        sourceLibrary = libName,
                        targetProject = project.Name,
                        copiedTypeCount = 0,
                        copiedFolderCount = 0
                    });
                    return;
                }

                int typeCount = 0, folderCount = 0;
                foreach (var s in sels)
                {
                    if (s is LibraryType) typeCount++;
                    else folderCount++;
                }

                if (useFullOverload)
                    globalLib.UpdateLibrary(sels, projLib, forceUpdateMode, deleteUnusedMode, structureMode);
                else
                    globalLib.UpdateLibrary(sels, projLib);

                SendJson(ctx.Response, 200, new
                {
                    message = "Global Library → Project Library 일괄 업데이트 완료 (네이티브 UpdateLibrary)",
                    sourceLibrary = libName,
                    targetProject = project.Name,
                    copiedTypeCount = typeCount,
                    copiedFolderCount = folderCount,
                    forceUpdate = useFullOverload ? forceUpdateMode.ToString() : "default(SetOnlyHigherUpdatedVersionAsDefault)",
                    deleteUnused = useFullOverload ? deleteUnusedMode.ToString() : "default(DoNotDelete)",
                    structure = useFullOverload ? structureMode.ToString() : "default(RetainStructure)"
                });
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException != null ? ex.InnerException.Message : null;
                SendJson(ctx.Response, 500, new
                {
                    error = "UpdateLibrary 실행 실패: " + ex.Message,
                    inner = inner,
                    type = ex.GetType().FullName,
                    hint = "ProjectLibrary 는 structure=RetainStructure 만 지원합니다"
                });
            }
        }

        /// <summary>
        /// API 2: 열린 Global Library → Project Library로 전체 복사
        /// </summary>
        static void GlobalLibraryToProjectLibrary(HttpListenerContext ctx)
        {
            string libName = ctx.Request.QueryString["lib"];
            string projectName = ctx.Request.QueryString["project"];
            string targetPath = ctx.Request.QueryString["targetPath"] ?? "";
            EnsureConnected();

            // 캐시 + connectedPortal.GlobalLibraries 양쪽에서 검색 (TIA Portal 에서 직접 연 라이브러리도 인식)
            ILibrary globalLib = ResolveGlobalLibraryByName(libName, out string _, out string resolvedName, out List<string> available);
            if (globalLib == null)
            {
                if (available.Count == 0)
                {
                    SendJson(ctx.Response, 404, new
                    {
                        error = "열려있는 Global Library가 없습니다. /api/global-library/import 또는 /api/global-library/open 으로 열어주세요. (TIA Portal 에서 직접 연 라이브러리도 인식됩니다)"
                    });
                    return;
                }
                if (string.IsNullOrEmpty(libName))
                {
                    SendJson(ctx.Response, 400, new
                    {
                        error = "여러 Global Library 중 어느 것을 사용할지 ?lib= 으로 지정하세요",
                        available = available
                    });
                    return;
                }
                SendJson(ctx.Response, 404, new
                {
                    error = "Global Library를 찾을 수 없습니다: " + libName,
                    available = available
                });
                return;
            }
            libName = resolvedName;

            Project project = null;
            foreach (Project p in connectedPortal.Projects)
            {
                if (projectName == null || p.Name == projectName) { project = p; break; }
            }
            if (project == null) { SendJson(ctx.Response, 404, new { error = "No project found" }); return; }

            var projLib = project.ProjectLibrary;

            var copiedMasterCopies = new List<object>();
            var copiedLibraryTypes = new List<object>();
            var errors = new List<object>();

            try
            {
                CopyAllMasterCopiesToProject(globalLib.MasterCopyFolder, "", projLib.MasterCopyFolder, targetPath, copiedMasterCopies, errors);
                CopyAllLibraryTypesToProject(globalLib.TypeFolder, "", projLib.TypeFolder, targetPath, copiedLibraryTypes, errors);

                SendJson(ctx.Response, 200, new {
                    message = "Global Library → Project Library 복사 완료",
                    lib = libName,
                    project = project.Name,
                    masterCopies = new { count = copiedMasterCopies.Count, items = copiedMasterCopies },
                    libraryTypes = new { count = copiedLibraryTypes.Count, items = copiedLibraryTypes },
                    errorCount = errors.Count,
                    errors
                });
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Project Library 복사 실패: " + ex.Message, type = ex.GetType().Name });
            }
        }

        static void CopyAllMasterCopiesToProject(MasterCopySystemFolder srcFolder, string srcPath,
            MasterCopySystemFolder destRoot, string baseTargetPath,
            List<object> results, List<object> errors)
        {
            string destPath = string.IsNullOrEmpty(baseTargetPath)
                ? srcPath
                : (string.IsNullOrEmpty(srcPath) ? baseTargetPath : baseTargetPath + "/" + srcPath);

            foreach (MasterCopy mc in srcFolder.MasterCopies)
            {
                try
                {
                    MasterCopyComposition target;
                    if (string.IsNullOrEmpty(destPath))
                    {
                        target = destRoot.MasterCopies;
                    }
                    else
                    {
                        var folder = NavigateOrCreateMasterCopyFolder(destRoot, destPath);
                        target = folder.MasterCopies;
                    }
                    var newMc = target.CreateFrom(mc);
                    results.Add(new { name = newMc.Name, path = destPath, kind = "masterCopy" });
                }
                catch (Exception ex)
                {
                    errors.Add(new { name = mc.Name, path = destPath, kind = "masterCopy", error = ex.Message });
                }
            }

            foreach (MasterCopyUserFolder sub in srcFolder.Folders)
            {
                string subPath = string.IsNullOrEmpty(srcPath) ? sub.Name : srcPath + "/" + sub.Name;
                CopyAllMasterCopiesUserToProject(sub, subPath, destRoot, baseTargetPath, results, errors);
            }
        }

        static void CopyAllMasterCopiesUserToProject(MasterCopyUserFolder srcFolder, string srcPath,
            MasterCopySystemFolder destRoot, string baseTargetPath,
            List<object> results, List<object> errors)
        {
            string destPath = string.IsNullOrEmpty(baseTargetPath)
                ? srcPath
                : (string.IsNullOrEmpty(srcPath) ? baseTargetPath : baseTargetPath + "/" + srcPath);

            foreach (MasterCopy mc in srcFolder.MasterCopies)
            {
                try
                {
                    MasterCopyComposition target;
                    if (string.IsNullOrEmpty(destPath))
                    {
                        target = destRoot.MasterCopies;
                    }
                    else
                    {
                        var folder = NavigateOrCreateMasterCopyFolder(destRoot, destPath);
                        target = folder.MasterCopies;
                    }
                    var newMc = target.CreateFrom(mc);
                    results.Add(new { name = newMc.Name, path = destPath, kind = "masterCopy" });
                }
                catch (Exception ex)
                {
                    errors.Add(new { name = mc.Name, path = destPath, kind = "masterCopy", error = ex.Message });
                }
            }

            foreach (MasterCopyUserFolder sub in srcFolder.Folders)
            {
                string subPath = srcPath + "/" + sub.Name;
                CopyAllMasterCopiesUserToProject(sub, subPath, destRoot, baseTargetPath, results, errors);
            }
        }

        static void CopyAllLibraryTypesToProject(LibraryTypeSystemFolder srcFolder, string srcPath,
            LibraryTypeSystemFolder destRoot, string baseTargetPath,
            List<object> results, List<object> errors)
        {
            string destPath = string.IsNullOrEmpty(baseTargetPath)
                ? srcPath
                : (string.IsNullOrEmpty(srcPath) ? baseTargetPath : baseTargetPath + "/" + srcPath);

            foreach (LibraryType lt in srcFolder.Types)
            {
                CopySingleLibraryType(lt, destPath, destRoot, results, errors);
            }

            foreach (LibraryTypeUserFolder sub in srcFolder.Folders)
            {
                string subPath = string.IsNullOrEmpty(srcPath) ? sub.Name : srcPath + "/" + sub.Name;
                CopyAllLibraryTypesUserToProject(sub, subPath, destRoot, baseTargetPath, results, errors);
            }
        }

        static void CopyAllLibraryTypesUserToProject(LibraryTypeUserFolder srcFolder, string srcPath,
            LibraryTypeSystemFolder destRoot, string baseTargetPath,
            List<object> results, List<object> errors)
        {
            string destPath = string.IsNullOrEmpty(baseTargetPath)
                ? srcPath
                : (string.IsNullOrEmpty(srcPath) ? baseTargetPath : baseTargetPath + "/" + srcPath);

            foreach (LibraryType lt in srcFolder.Types)
            {
                CopySingleLibraryType(lt, destPath, destRoot, results, errors);
            }

            foreach (LibraryTypeUserFolder sub in srcFolder.Folders)
            {
                string subPath = srcPath + "/" + sub.Name;
                CopyAllLibraryTypesUserToProject(sub, subPath, destRoot, baseTargetPath, results, errors);
            }
        }

        static void CopySingleLibraryType(LibraryType lt, string destPath,
            LibraryTypeSystemFolder destRoot, List<object> results, List<object> errors)
        {
            LibraryTypeVersion latestVersion = null;
            foreach (LibraryTypeVersion v in lt.Versions) { latestVersion = v; }
            if (latestVersion == null)
            {
                errors.Add(new { name = lt.Name, path = destPath, kind = "libraryType", error = "버전 없음" });
                return;
            }

            string docDir = Path.Combine(tempDir, "glib_import_" + Guid.NewGuid().ToString().Substring(0, 8));
            Directory.CreateDirectory(docDir);

            try
            {
                string format = GetLibraryTypeExportFormat(lt);
                LibraryTypeComposition typeTarget;
                if (string.IsNullOrEmpty(destPath))
                {
                    typeTarget = destRoot.Types;
                }
                else
                {
                    var folder = NavigateOrCreateLibraryTypeFolder(destRoot, destPath);
                    typeTarget = folder.Types;
                }

                if (format != null)
                {
                    string fileNameNoExt = MakeValidFileName(lt.Name);
                    latestVersion.ExportAsDocuments(new DirectoryInfo(docDir), fileNameNoExt, format, LibraryExportOptions.None);
                    typeTarget.CreateFromDocuments(new DirectoryInfo(docDir), fileNameNoExt, LibraryImportOptions.None);
                    results.Add(new { name = lt.Name, path = destPath, kind = "libraryType", method = "documents" });
                }
                else
                {
                    // Documents 미지원 → Export XML fallback
                    string xmlFile = Path.Combine(docDir, MakeValidFileName(lt.Name) + ".xml");
                    latestVersion.Export(new FileInfo(xmlFile), ExportOptions.WithDefaults);
                    errors.Add(new { name = lt.Name, path = destPath, kind = "libraryType",
                        error = "Documents 포맷 미지원. XML로 export 됨 — PLC import 후 수동 추가 필요",
                        exportedXml = xmlFile });
                }
            }
            catch (Exception ex)
            {
                errors.Add(new { name = lt.Name, path = destPath, kind = "libraryType", error = ex.Message });
            }
            finally
            {
                if (Directory.Exists(docDir)) try { Directory.Delete(docDir, true); } catch { }
            }
        }

        static void OpenGlobalLibrary(HttpListenerContext ctx)
        {
            EnsureConnected();

            byte[] body;
            using (var ms = new MemoryStream())
            {
                ctx.Request.InputStream.CopyTo(ms);
                body = ms.ToArray();
            }
            string bodyStr = Encoding.UTF8.GetString(body);
            var parsed = json.Deserialize<Dictionary<string, object>>(bodyStr);

            string filePath = parsed != null && parsed.ContainsKey("path") ? parsed["path"].ToString() : null;
            string mode = parsed != null && parsed.ContainsKey("mode") ? parsed["mode"].ToString().ToLower() : "readonly";

            if (string.IsNullOrEmpty(filePath))
            {
                SendJson(ctx.Response, 400, new { error = "JSON body에 path 필드가 필요합니다. 예: {\"path\": \"C:\\\\libs\\\\MyLib.al20\"}" });
                return;
            }

            if (!File.Exists(filePath))
            {
                SendJson(ctx.Response, 404, new { error = "파일을 찾을 수 없습니다: " + filePath });
                return;
            }

            string libName = Path.GetFileNameWithoutExtension(filePath);

            // 이미 열려있으면 기존 것 반환
            if (openedGlobalLibraries.ContainsKey(libName))
            {
                SendJson(ctx.Response, 200, new { message = "이미 열려있는 Global Library입니다", name = libName, path = filePath });
                return;
            }

            try
            {
                OpenMode openMode = mode == "readwrite" ? OpenMode.ReadWrite : OpenMode.ReadOnly;
                UserGlobalLibrary globalLib = connectedPortal.GlobalLibraries.Open(new FileInfo(filePath), openMode);
                openedGlobalLibraries[libName] = globalLib;

                SendJson(ctx.Response, 200, new {
                    message = "Global Library 열기 성공",
                    name = libName,
                    path = filePath,
                    mode = mode
                });
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Global Library 열기 실패: " + ex.Message, type = ex.GetType().Name });
            }
        }

        static void ListGlobalLibrary(HttpListenerContext ctx)
        {
            string libName = ctx.Request.QueryString["name"];
            EnsureConnected();

            if (openedGlobalLibraries.Count == 0)
            {
                SendJson(ctx.Response, 404, new { error = "열려있는 Global Library가 없습니다. /api/global-library/open으로 먼저 열어주세요." });
                return;
            }

            // name 미지정 → 열린 라이브러리 목록 반환
            if (string.IsNullOrEmpty(libName))
            {
                var libs = new List<object>();
                foreach (var kvp in openedGlobalLibraries)
                {
                    var gl = kvp.Value;
                    int mcCount = 0, ltCount = 0;
                    foreach (MasterCopy mc in gl.MasterCopyFolder.MasterCopies) mcCount++;
                    foreach (MasterCopyUserFolder sub in gl.MasterCopyFolder.Folders) mcCount += CountMasterCopiesInFolder(sub);
                    foreach (LibraryType lt in gl.TypeFolder.Types) ltCount++;
                    foreach (LibraryTypeUserFolder sub in gl.TypeFolder.Folders) ltCount += CountLibraryTypesInFolder(sub);
                    libs.Add(new { name = kvp.Key, masterCopyCount = mcCount, libraryTypeCount = ltCount });
                }
                SendJson(ctx.Response, 200, new { openedLibraries = libs });
                return;
            }

            if (!openedGlobalLibraries.ContainsKey(libName))
            {
                SendJson(ctx.Response, 404, new { error = "열려있지 않은 Global Library: " + libName, opened = new List<string>(openedGlobalLibraries.Keys) });
                return;
            }

            var lib = openedGlobalLibraries[libName];

            var masterCopies = new List<object>();
            CollectMasterCopies(lib.MasterCopyFolder, "", masterCopies);

            var libraryTypes = new List<object>();
            CollectLibraryTypes(lib.TypeFolder, "", libraryTypes);

            SendJson(ctx.Response, 200, new {
                name = libName,
                masterCopyCount = masterCopies.Count,
                masterCopies,
                libraryTypeCount = libraryTypes.Count,
                libraryTypes
            });
        }

        static int CountMasterCopiesInFolder(MasterCopyUserFolder folder)
        {
            int count = 0;
            foreach (MasterCopy mc in folder.MasterCopies) count++;
            foreach (MasterCopyUserFolder sub in folder.Folders) count += CountMasterCopiesInFolder(sub);
            return count;
        }

        static int CountLibraryTypesInFolder(LibraryTypeUserFolder folder)
        {
            int count = 0;
            foreach (LibraryType lt in folder.Types) count++;
            foreach (LibraryTypeUserFolder sub in folder.Folders) count += CountLibraryTypesInFolder(sub);
            return count;
        }

        static void CopyGlobalLibraryToProject(HttpListenerContext ctx)
        {
            string libName = ctx.Request.QueryString["lib"];
            string itemName = ctx.Request.QueryString["name"];
            string itemPath = ctx.Request.QueryString["path"] ?? "";
            string targetKind = (ctx.Request.QueryString["target"] ?? "project-library").ToLower();
            string plcName = ctx.Request.QueryString["plc"];
            string projectName = ctx.Request.QueryString["project"];
            string targetPath = ctx.Request.QueryString["targetPath"] ?? "";
            EnsureConnected();

            if (string.IsNullOrEmpty(libName) || string.IsNullOrEmpty(itemName))
            {
                SendJson(ctx.Response, 400, new { error = "?lib= (Global Library 이름) 과 ?name= (항목 이름) 파라미터가 필요합니다" });
                return;
            }

            if (!openedGlobalLibraries.ContainsKey(libName))
            {
                SendJson(ctx.Response, 404, new { error = "열려있지 않은 Global Library: " + libName, opened = new List<string>(openedGlobalLibraries.Keys) });
                return;
            }

            var globalLib = openedGlobalLibraries[libName];

            Project project = null;
            foreach (Project p in connectedPortal.Projects)
            {
                if (projectName == null || p.Name == projectName) { project = p; break; }
            }
            if (project == null) { SendJson(ctx.Response, 404, new { error = "No project found" }); return; }

            try
            {
                // MasterCopy 찾기
                var mc = FindMasterCopy(globalLib.MasterCopyFolder, itemName, itemPath);
                if (mc != null)
                {
                    if (targetKind == "plc")
                    {
                        // MasterCopy → PLC 블록으로 복사
                        if (string.IsNullOrEmpty(plcName))
                        {
                            SendJson(ctx.Response, 400, new { error = "target=plc 일 때는 ?plc= 파라미터가 필요합니다" });
                            return;
                        }
                        var plc = FindPlc(plcName);
                        if (plc == null) { SendJson(ctx.Response, 404, new { error = "PLC not found: " + plcName }); return; }

                        PlcBlockGroup targetGroup = plc.BlockGroup;
                        if (!string.IsNullOrEmpty(targetPath))
                        {
                            targetGroup = NavigateOrCreateBlockGroup(plc.BlockGroup, targetPath);
                        }
                        targetGroup.Blocks.CreateFrom(mc);
                        SendJson(ctx.Response, 200, new {
                            message = "Global Library MasterCopy → PLC 블록 복사 완료",
                            name = mc.Name, lib = libName, plc = plcName, group = targetPath
                        });
                    }
                    else
                    {
                        // MasterCopy → Project Library의 MasterCopy로 복사
                        var projLib = project.ProjectLibrary;
                        MasterCopyComposition mcTarget;
                        if (string.IsNullOrEmpty(targetPath))
                        {
                            mcTarget = projLib.MasterCopyFolder.MasterCopies;
                        }
                        else
                        {
                            var folder = NavigateOrCreateMasterCopyFolder(projLib.MasterCopyFolder, targetPath);
                            if (folder == null) { SendJson(ctx.Response, 500, new { error = "폴더 생성 실패: " + targetPath }); return; }
                            mcTarget = folder.MasterCopies;
                        }
                        var newMc = mcTarget.CreateFrom(mc);
                        SendJson(ctx.Response, 200, new {
                            message = "Global Library MasterCopy → Project Library 복사 완료",
                            name = newMc.Name, lib = libName, targetPath
                        });
                    }
                    return;
                }

                // LibraryType 찾기
                var lt = FindLibraryType(globalLib.TypeFolder, itemName, itemPath);
                if (lt != null)
                {
                    LibraryTypeVersion latestVersion = null;
                    foreach (LibraryTypeVersion v in lt.Versions) { latestVersion = v; }
                    if (latestVersion == null)
                    {
                        SendJson(ctx.Response, 404, new { error = "타입에 버전이 없습니다: " + itemName });
                        return;
                    }

                    if (targetKind == "plc")
                    {
                        // LibraryType → PLC 타입으로 복사 (export XML → import)
                        if (string.IsNullOrEmpty(plcName))
                        {
                            SendJson(ctx.Response, 400, new { error = "target=plc 일 때는 ?plc= 파라미터가 필요합니다" });
                            return;
                        }
                        var plc = FindPlc(plcName);
                        if (plc == null) { SendJson(ctx.Response, 404, new { error = "PLC not found: " + plcName }); return; }

                        string tempExport = Path.Combine(tempDir, MakeValidFileName(itemName) + ".xml");
                        if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                        if (File.Exists(tempExport)) File.Delete(tempExport);

                        latestVersion.Export(new FileInfo(tempExport), ExportOptions.WithDefaults);
                        string shortPath = CopyToFlat(tempExport, @"C:\T\glibcopy");
                        File.Delete(tempExport);

                        try
                        {
                            plc.TypeGroup.Types.Import(new FileInfo(shortPath), ImportOptions.Override);
                            SendJson(ctx.Response, 200, new {
                                message = "Global Library LibraryType → PLC 타입 복사 완료",
                                name = lt.Name, lib = libName, plc = plcName
                            });
                        }
                        finally { if (File.Exists(shortPath)) File.Delete(shortPath); }
                    }
                    else
                    {
                        // LibraryType → Project Library 타입으로 복사 (ExportAsDocuments → CreateFromDocuments)
                        var projLib = project.ProjectLibrary;
                        LibraryTypeComposition typeTarget;
                        if (string.IsNullOrEmpty(targetPath))
                        {
                            typeTarget = projLib.TypeFolder.Types;
                        }
                        else
                        {
                            var folder = NavigateOrCreateLibraryTypeFolder(projLib.TypeFolder, targetPath);
                            if (folder == null) { SendJson(ctx.Response, 500, new { error = "폴더 생성 실패: " + targetPath }); return; }
                            typeTarget = folder.Types;
                        }

                        string docDir = Path.Combine(tempDir, "glib_type_" + Guid.NewGuid().ToString().Substring(0, 8));
                        Directory.CreateDirectory(docDir);
                        string fileNameNoExt = MakeValidFileName(lt.Name);

                        try
                        {
                            string format = GetLibraryTypeExportFormat(lt);
                            if (format != null)
                            {
                                latestVersion.ExportAsDocuments(new DirectoryInfo(docDir), fileNameNoExt, format, LibraryExportOptions.None);
                                typeTarget.CreateFromDocuments(new DirectoryInfo(docDir), fileNameNoExt, LibraryImportOptions.None);
                            }
                            else
                            {
                                // ExportAsDocuments 미지원 → Export XML → PLC import 안내
                                SendJson(ctx.Response, 400, new {
                                    error = "이 LibraryType은 Documents 포맷을 지원하지 않아 직접 복사할 수 없습니다. target=plc로 PLC에 직접 복사하세요.",
                                    hint = "?target=plc&plc=PLC_1"
                                });
                                return;
                            }
                        }
                        finally
                        {
                            if (Directory.Exists(docDir)) try { Directory.Delete(docDir, true); } catch { }
                        }

                        SendJson(ctx.Response, 200, new {
                            message = "Global Library LibraryType → Project Library 복사 완료",
                            name = lt.Name, lib = libName, targetPath
                        });
                    }
                    return;
                }

                SendJson(ctx.Response, 404, new { error = "Global Library에서 항목을 찾을 수 없습니다: " + itemName });
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Global Library 복사 실패: " + ex.Message, type = ex.GetType().Name });
            }
        }

        static void CloseGlobalLibrary(HttpListenerContext ctx)
        {
            string libName = ctx.Request.QueryString["name"];

            if (string.IsNullOrEmpty(libName))
            {
                // 전체 닫기
                var closed = new List<string>();
                foreach (var kvp in new Dictionary<string, UserGlobalLibrary>(openedGlobalLibraries))
                {
                    try { kvp.Value.Close(); } catch { }
                    CleanupGlobalLibTempDir(kvp.Key);
                    closed.Add(kvp.Key);
                }
                openedGlobalLibraries.Clear();
                SendJson(ctx.Response, 200, new { message = "모든 Global Library 닫기 완료", closed });
                return;
            }

            if (!openedGlobalLibraries.ContainsKey(libName))
            {
                SendJson(ctx.Response, 404, new { error = "열려있지 않은 Global Library: " + libName });
                return;
            }

            try
            {
                openedGlobalLibraries[libName].Close();
                openedGlobalLibraries.Remove(libName);
                CleanupGlobalLibTempDir(libName);
                SendJson(ctx.Response, 200, new { message = "Global Library 닫기 완료", name = libName });
            }
            catch (Exception ex)
            {
                SendJson(ctx.Response, 500, new { error = "Global Library 닫기 실패: " + ex.Message, type = ex.GetType().Name });
            }
        }

        static void CleanupGlobalLibTempDir(string libName)
        {
            // tempDir (기존 호환)
            string tempLibDir = Path.Combine(tempDir, libName);
            if (Directory.Exists(tempLibDir)) try { Directory.Delete(tempLibDir, true); } catch { }

            // global_libraries 하위 extract 폴더 정리
            string extractDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "global_libraries", libName + "_extract");
            if (Directory.Exists(extractDir)) try { Directory.Delete(extractDir, true); } catch { }
        }

        static MasterCopy FindMasterCopy(MasterCopySystemFolder folder, string name, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                foreach (MasterCopy mc in folder.MasterCopies)
                    if (mc.Name == name) return mc;
                foreach (MasterCopyUserFolder sub in folder.Folders)
                {
                    var found = FindMasterCopyInUser(sub, name);
                    if (found != null) return found;
                }
                return null;
            }

            string[] parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            MasterCopyUserFolder current = null;
            var folders = folder.Folders;
            foreach (string part in parts)
            {
                current = null;
                foreach (MasterCopyUserFolder sub in folders)
                {
                    if (sub.Name == part) { current = sub; break; }
                }
                if (current == null) return null;
                folders = current.Folders;
            }
            if (current != null)
            {
                foreach (MasterCopy mc in current.MasterCopies)
                    if (mc.Name == name) return mc;
            }
            return null;
        }

        static MasterCopy FindMasterCopyInUser(MasterCopyUserFolder folder, string name)
        {
            foreach (MasterCopy mc in folder.MasterCopies)
                if (mc.Name == name) return mc;
            foreach (MasterCopyUserFolder sub in folder.Folders)
            {
                var found = FindMasterCopyInUser(sub, name);
                if (found != null) return found;
            }
            return null;
        }

        static LibraryType FindLibraryType(LibraryTypeSystemFolder folder, string name, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                foreach (LibraryType lt in folder.Types)
                    if (lt.Name == name) return lt;
                foreach (LibraryTypeUserFolder sub in folder.Folders)
                {
                    var found = FindLibraryTypeInUser(sub, name);
                    if (found != null) return found;
                }
                return null;
            }

            string[] parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            LibraryTypeUserFolder current = null;
            var folders = folder.Folders;
            foreach (string part in parts)
            {
                current = null;
                foreach (LibraryTypeUserFolder sub in folders)
                {
                    if (sub.Name == part) { current = sub; break; }
                }
                if (current == null) return null;
                folders = current.Folders;
            }
            if (current != null)
            {
                foreach (LibraryType lt in current.Types)
                    if (lt.Name == name) return lt;
            }
            return null;
        }

        static LibraryType FindLibraryTypeInUser(LibraryTypeUserFolder folder, string name)
        {
            foreach (LibraryType lt in folder.Types)
                if (lt.Name == name) return lt;
            foreach (LibraryTypeUserFolder sub in folder.Folders)
            {
                var found = FindLibraryTypeInUser(sub, name);
                if (found != null) return found;
            }
            return null;
        }

        static MasterCopyUserFolder NavigateOrCreateMasterCopyFolder(MasterCopySystemFolder root, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string[] parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            MasterCopyUserFolder current = null;

            string firstPart = parts[0];
            foreach (MasterCopyUserFolder sub in root.Folders)
            {
                if (sub.Name == firstPart) { current = sub; break; }
            }
            if (current == null)
            {
                try { current = root.Folders.Create(firstPart); } catch { return null; }
            }

            for (int i = 1; i < parts.Length; i++)
            {
                MasterCopyUserFolder next = null;
                foreach (MasterCopyUserFolder sub in current.Folders)
                {
                    if (sub.Name == parts[i]) { next = sub; break; }
                }
                if (next == null)
                {
                    try { next = current.Folders.Create(parts[i]); } catch { break; }
                }
                current = next;
            }
            return current;
        }

        static LibraryTypeUserFolder NavigateOrCreateLibraryTypeFolder(LibraryTypeSystemFolder root, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string[] parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            LibraryTypeUserFolder current = null;

            string firstPart = parts[0];
            foreach (LibraryTypeUserFolder sub in root.Folders)
            {
                if (sub.Name == firstPart) { current = sub; break; }
            }
            if (current == null)
            {
                try { current = root.Folders.Create(firstPart); } catch { return null; }
            }

            for (int i = 1; i < parts.Length; i++)
            {
                LibraryTypeUserFolder next = null;
                foreach (LibraryTypeUserFolder sub in current.Folders)
                {
                    if (sub.Name == parts[i]) { next = sub; break; }
                }
                if (next == null)
                {
                    try { next = current.Folders.Create(parts[i]); } catch { break; }
                }
                current = next;
            }
            return current;
        }

        // ── Compile ──

        // PLC / HMI 공통 컴파일 — Software (전달받은 target) + Hardware (resourceName 기반 DeviceItem 의 ICompilable chain)
        // resourceName: ?plc= 또는 ?hmi= 의 DeviceItem 이름
        // softwareTarget: PlcSoftware (IEngineeringServiceProvider 구현) / HmiTarget / HmiSoftware (직접 구현 안 함 — reflection 으로 GetService 호출)
        // softwareLabel: 응답에 표시할 라벨
        static void CompileResource(HttpListenerContext ctx, string resourceName, object softwareTarget, string softwareLabel)
        {
            // ── 1) Software 컴파일 ──
            object softwareResult;
            bool softwareOk = false;
            try
            {
                ICompilable sw = TryGetCompilable(softwareTarget);
                if (sw == null)
                {
                    softwareResult = new { error = softwareLabel + " 가 ICompilable 서비스 미지원", success = false };
                }
                else
                {
                    var r = sw.Compile();
                    softwareResult = BuildCompileResultPayload(r, "software", softwareLabel, null);
                    softwareOk = r.State == CompilerResultState.Success || r.State == CompilerResultState.Warning;
                }
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException != null ? ex.InnerException.Message : null;
                softwareResult = new { error = "Software 컴파일 실행 실패: " + ex.Message, inner, success = false };
            }

            // ── 2) Hardware 컴파일 ──
            object hardwareResult;
            bool hardwareOk = false;
            var triedTargets = new List<string>();
            try
            {
                // resourceName(?plc= or ?hmi=) → DeviceItem 직접 매칭
                DeviceItem cpuItem = null;
                Device cpuDevice = null;
                foreach (Project proj in connectedPortal.Projects)
                {
                    foreach (Device dev in proj.Devices)
                    {
                        cpuItem = FindDeviceItemNamedRecursive2(dev.DeviceItems, resourceName);
                        if (cpuItem != null) { cpuDevice = dev; break; }
                    }
                    if (cpuItem != null) break;
                }
                if (cpuItem == null)
                {
                    hardwareResult = new { error = "DeviceItem 을 찾을 수 없음: " + resourceName, success = false };
                }
                else
                {
                    // ICompilable 후보 chain — Device 우선, 부모 chain, 마지막으로 DeviceItem 자체
                    var candidates = new List<KeyValuePair<string, object>>();
                    if (cpuDevice != null)
                        candidates.Add(new KeyValuePair<string, object>(
                            "Device(" + cpuDevice.Name + ")", cpuDevice));
                    var p = cpuItem.Parent;
                    while (p != null)
                    {
                        if (p is DeviceItem pdi)
                            candidates.Add(new KeyValuePair<string, object>(
                                "DeviceItem.Parent(" + pdi.Name + ")", pdi));
                        else if (p is Device pdev)
                            candidates.Add(new KeyValuePair<string, object>(
                                "Device(" + pdev.Name + ")", pdev));
                        try { p = p.Parent; } catch { p = null; }
                    }
                    candidates.Add(new KeyValuePair<string, object>(
                        "DeviceItem(" + cpuItem.Name + ")", cpuItem));

                    ICompilable hwCompilable = null;
                    string usedTarget = null;
                    foreach (var c in candidates)
                    {
                        triedTargets.Add(c.Key);
                        try
                        {
                            var svc = TryGetCompilable(c.Value);
                            if (svc != null) { hwCompilable = svc; usedTarget = c.Key; break; }
                        }
                        catch (Exception ex)
                        {
                            triedTargets[triedTargets.Count - 1] += " (err: " + ex.Message + ")";
                        }
                    }

                    if (hwCompilable == null)
                    {
                        hardwareResult = new
                        {
                            error = "Hardware 컴파일을 위한 ICompilable 서비스를 찾을 수 없음",
                            deviceItem = cpuItem.Name,
                            device = cpuDevice != null ? cpuDevice.Name : null,
                            triedTargets,
                            success = false
                        };
                    }
                    else
                    {
                        var r = hwCompilable.Compile();
                        hardwareResult = BuildCompileResultPayload(r, "hardware", usedTarget, triedTargets);
                        hardwareOk = r.State == CompilerResultState.Success || r.State == CompilerResultState.Warning;
                    }
                }
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException != null ? ex.InnerException.Message : null;
                hardwareResult = new { error = "Hardware 컴파일 실행 실패: " + ex.Message, inner, triedTargets, success = false };
            }

            SendJson(ctx.Response, 200, new
            {
                resource = resourceName,
                software = softwareResult,
                hardware = hardwareResult,
                success = softwareOk && hardwareOk
            });
        }

        // GetService<ICompilable>() 호출. HmiSoftware 처럼 IEngineeringServiceProvider 를 직접 구현하지 않는
        // 타입에서도 reflection 으로 같은 시그니처의 GetService<T> 메서드를 호출.
        static ICompilable TryGetCompilable(object target)
        {
            if (target == null) return null;
            // 1) typed cast
            var sip = target as IEngineeringServiceProvider;
            if (sip != null)
            {
                try { return sip.GetService<ICompilable>(); } catch { }
            }
            // 2) reflection — 타겟 타입에 GetService<T>() 가 명시적 구현 또는 자체 메서드로 있을 수도 있음
            try
            {
                var m = target.GetType().GetMethod("GetService", BindingFlags.Public | BindingFlags.Instance);
                if (m != null && m.IsGenericMethodDefinition)
                {
                    var generic = m.MakeGenericMethod(typeof(ICompilable));
                    return generic.Invoke(target, null) as ICompilable;
                }
            }
            catch { }
            return null;
        }

        // FindDeviceItemNamedRecursive 와 같은 동작 — 컴파일 컨텍스트에서 사용
        static DeviceItem FindDeviceItemNamedRecursive2(DeviceItemComposition items, string name)
        {
            foreach (DeviceItem di in items)
            {
                if (di.Name == name) return di;
                var found = FindDeviceItemNamedRecursive2(di.DeviceItems, name);
                if (found != null) return found;
            }
            return null;
        }

        static object BuildCompileMessage(CompilerResultMessage msg)
        {
            var children = new List<object>();
            try
            {
                foreach (CompilerResultMessage child in msg.Messages)
                    children.Add(BuildCompileMessage(child));
            }
            catch { }
            return new
            {
                path = msg.Path,
                description = msg.Description,
                state = msg.State.ToString(),
                dateTime = msg.DateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                errorCount = msg.ErrorCount,
                warningCount = msg.WarningCount,
                childCount = children.Count,
                children = children
            };
        }

        static object BuildCompileResultPayload(CompilerResult result, string mode, string usedTarget, List<string> triedTargets)
        {
            var messages = new List<object>();
            foreach (CompilerResultMessage msg in result.Messages)
                messages.Add(BuildCompileMessage(msg));
            return new
            {
                state = result.State.ToString(),
                errorCount = result.ErrorCount,
                warningCount = result.WarningCount,
                mode = mode,
                usedTarget = usedTarget,
                triedTargets = triedTargets,
                success = result.State == CompilerResultState.Success || result.State == CompilerResultState.Warning,
                messageCount = messages.Count,
                messages = messages
            };
        }

        // ── Helpers ──

        static void SendJson(HttpListenerResponse resp, int status, object data)
        {
            resp.StatusCode = status;
            resp.ContentType = "application/json; charset=utf-8";
            byte[] buf = Encoding.UTF8.GetBytes(json.Serialize(data));
            resp.ContentLength64 = buf.Length;
            resp.OutputStream.Write(buf, 0, buf.Length);
            resp.Close();
        }

        static void ServeOpenApiSpec(HttpListenerResponse resp)
        {
            string spec = @"{
  ""openapi"": ""3.0.3"",
  ""info"": {
    ""title"": ""TIA Portal Openness REST API"",
    ""version"": ""1.0.0"",
    ""description"": ""Siemens TIA Portal Openness (V19 / V20 / V21) 를 위한 REST API 브릿지입니다.\n\n실행 중인 TIA Portal 인스턴스에 연결하여 프로젝트 데이터를 조회하고, PLC 블록을 Import/Export 할 수 있습니다.\n\n---\n\n## 사전 요구사항\n\n| 항목 | 설명 |\n|---|---|\n| **TIA Portal V19 / V20 / V21** | 대상 버전이 로컬 PC에 설치되어 있어야 합니다 |\n| **.NET Framework 4.8** | 런타임 필수 |\n| **Siemens TIA Openness** | Windows 그룹 'Siemens TIA Openness'에 사용자가 등록되어 있어야 합니다 |\n| **TiaApiServer.V{버전}.exe** | 대상 버전에 맞는 실행파일이 실행 중이어야 합니다 (기본 포트: 8099). V21 이면 TiaApiServer.V21.exe |\n\n## 주의사항\n\n- 이 API는 **로컬 PC에서만 동작**합니다. TIA Portal이 설치된 PC에서 TiaApiServer.exe를 실행한 뒤 localhost:8099로 접근해야 합니다.\n- 원격 서버에서는 사용할 수 없습니다. TIA Portal Openness API 자체가 로컬 프로세스 간 통신만 지원합니다.\n- API 서버를 먼저 실행한 후, /api/connect를 호출하여 TIA Portal 프로세스에 연결해야 다른 API를 사용할 수 있습니다.""
  },
  ""servers"": [{""url"": ""http://localhost:" + port + @""", ""description"": ""로컬 TIA API 서버""}],
  ""tags"": [
    {""name"": ""Connection"", ""description"": ""TIA Portal 프로세스 연결 관리""},
    {""name"": ""Setup"", ""description"": ""TIA Portal / 프로젝트 / 디바이스 신규 생성""},
    {""name"": ""Project"", ""description"": ""프로젝트 및 디바이스 정보 조회""},
    {""name"": ""Catalog"", ""description"": ""하드웨어 카탈로그 검색 및 호환성 확인""},
    {""name"": ""Device Explorer"", ""description"": ""디바이스 아이템 트리 및 속성 탐색""},
    {""name"": ""Drive"", ""description"": ""드라이브 오브젝트, 파라미터, 모터 설정 (Startdrive)""},
    {""name"": ""PLC"", ""description"": ""PLC 블록, 데이터 타입, 태그, Software Unit 조회""},
    {""name"": ""Export"", ""description"": ""PLC 프로그램 내보내기 (SCL/DB/STL/XML ZIP)""},
    {""name"": ""Import"", ""description"": ""PLC 프로그램 가져오기 (XML/ZIP)""},
    {""name"": ""Compile"", ""description"": ""PLC 소프트웨어/하드웨어 컴파일 및 검증""},
    {""name"": ""HMI"", ""description"": ""HMI 태그, 텍스트 리스트, 스크린 조회 및 Import/Export""},
    {""name"": ""DLL"", ""description"": ""Openness 어셈블리 - 탐지 상태 확인 및 대상 TIA 버전 전환""},
    {""name"": ""GSD"", ""description"": ""GSDML 파일 관리 및 파싱 → 카탈로그 DB JSON 변환 (TIA Portal 불필요)""},
    {""name"": ""Library"", ""description"": ""프로젝트 라이브러리 (Master Copy / Library Type) 조회, Export, Import""},
    {""name"": ""AML"", ""description"": ""AML (AutomationML / CAx) 파일 Import/Export — 하드웨어 구성 교환""},
    {""name"": ""Global Library"", ""description"": ""Global Library (.al20) 파일 열기, 내용 조회, 프로젝트로 복사, 닫기""}
  ],
  ""paths"": {
    ""/api/gsd/list"": {
      ""get"": {
        ""tags"": [""GSD""],
        ""summary"": ""설치된 GSDML 파일 목록 조회"",
        ""description"": ""TIA Portal에 설치된 GSDML 파일 목록을 조회합니다. 기본 경로: C:\\\\ProgramData\\\\Siemens\\\\Automation\\\\Portal V{대상버전}\\\\data\\\\xdd\\\\gsd\\\\ (실행 중인 바이너리의 대상 버전을 따릅니다)\\n\\n**TIA Portal이 실행 중이지 않아도 사용 가능합니다.**"",
        ""parameters"": [
          {""name"": ""path"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""GSD 파일 검색 경로 (생략 시 기본 TIA Portal GSD 경로)""}
        ],
        ""responses"": {
          ""200"": {""description"": ""GSDML 파일 목록 (파일명, 벤더, 크기, 경로)""},
          ""404"": {""description"": ""GSD 디렉토리를 찾을 수 없음""}
        }
      }
    },
    ""/api/gsd/read"": {
      ""get"": {
        ""tags"": [""GSD""],
        ""summary"": ""GSDML XML 원본 조회"",
        ""description"": ""특정 GSDML 파일의 XML 원본을 반환합니다. /api/gsd/list에서 조회한 파일명 또는 전체 경로를 사용하세요."",
        ""parameters"": [
          {""name"": ""file"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""GSDML 파일명 또는 전체 경로 (예: gsdml-v2.32-keyence-sr-2000-20170110.xml)""}
        ],
        ""responses"": {
          ""200"": {""description"": ""GSDML XML 원본 (application/xml)""},
          ""404"": {""description"": ""파일을 찾을 수 없음""}
        }
      }
    },
    ""/api/gsd/parse"": {
      ""post"": {
        ""tags"": [""GSD""],
        ""summary"": ""GSDML 파일 → 카탈로그 DB JSON 변환"",
        ""description"": ""GSDML XML 파일을 업로드하면 GSDML 스펙에 정의된 모든 DAP/모듈/인터페이스/포트를 파싱하여 tia_catalog.db에 INSERT 가능한 JSON을 반환합니다.\\n\\n**TIA Portal이 실행 중이지 않아도 사용 가능합니다.**\\n\\n### 사용 방법\\n1. /api/gsd/list로 GSDML 파일 목록 조회\\n2. /api/gsd/read로 GSDML XML 조회 또는 직접 파일 업로드\\n3. 이 API로 카탈로그 JSON 변환\\n4. 반환된 JSON을 tia_catalog.db에 INSERT\\n\\n### 반환 데이터\\nhw_catalog, hw_device, hw_version (DAP + 모든 모듈), hw_interface, hw_port, hw_rack_mapping, hw_slot\\n\\n### 헤더\\nX-Filename 헤더에 GSDML 파일명을 설정하면 type_identifier 생성에 사용됩니다."",
        ""parameters"": [
          {""name"": ""startCatalogId"", ""in"": ""query"", ""schema"": {""type"": ""integer"", ""default"": 10001}, ""description"": ""hw_catalog ID 시작값 (기존 DB와 충돌 방지)""},
          {""name"": ""startDeviceId"", ""in"": ""query"", ""schema"": {""type"": ""integer"", ""default"": 10001}, ""description"": ""hw_device ID 시작값""},
          {""name"": ""parentCatalogId"", ""in"": ""query"", ""schema"": {""type"": ""integer"", ""default"": 3285}, ""description"": ""부모 카탈로그 ID (기본: Other field devices > PROFINET IO)""}
        ],
        ""requestBody"": {
          ""required"": true,
          ""content"": {
            ""application/xml"": {
              ""schema"": {""type"": ""string"", ""format"": ""binary""}
            },
            ""application/octet-stream"": {
              ""schema"": {""type"": ""string"", ""format"": ""binary""}
            }
          }
        },
        ""responses"": {
          ""200"": {""description"": ""GSD 디바이스 카탈로그 JSON (hw_catalog, hw_device, hw_version, hw_interface, hw_port, hw_rack_mapping, hw_slot)""},
          ""400"": {""description"": ""유효하지 않은 GSDML 파일""},
          ""500"": {""description"": ""GSDML 파싱 실패""}
        }
      }
    },
    ""/api/dll/status"": {
      ""get"": {
        ""tags"": [""DLL""],
        ""summary"": ""Openness 어셈블리 탐지 상태 확인"",
        ""description"": ""Openness 어셈블리(Siemens.Engineering* )를 설치된 TIA Portal 에서 찾았는지 확인합니다. 어셈블리는 이 저장소에 포함되지 않으며, 해당 버전의 TIA Portal 설치 경로에서만 참조합니다."",
        ""responses"": {""200"": {""description"": ""DLL 상태 정보 - 각 DLL의 존재 여부와 경로""}}
      }
    },
    ""/api/dll/versions"": {
      ""get"": {
        ""tags"": [""DLL""],
        ""summary"": ""TIA 버전 목록 / 현재 대상 버전"",
        ""description"": ""설치된 TIA Portal 버전, 현재 대상 버전(active), 실제 로드된 버전(loaded), 실행 중인 포탈 버전을 반환합니다.\\n\\n.NET 은 한 프로세스에 같은 어셈블리를 한 번만 로드하고 교체할 수 없어서, Openness 가 이미 로드된 뒤에는 버전을 바꿀 수 없습니다 (canSwitch=false). 그때는 프로세스를 다시 띄워야 합니다:\\n\\n```\\nTiaApiServer.V19.exe 8099 --tia=19\\n```\\n\\n버전마다 Openness 어셈블리 이름이 달라 한 바이너리가 여러 버전을 겸할 수 없습니다. V19/V20 은 TiaApiServer.V19/V20.exe, V21 은 TiaApiServer.V21.exe 를 쓰세요. (V21 은 Base/Step7/WinCC/WinCCUnified/Startdrive 로 분해돼 있습니다.)"",
        ""responses"": {""200"": {""description"": ""버전 목록 - installed/running/active/loaded/supported 플래그 포함""}}
      }
    },
    ""/api/dll/switch"": {
      ""put"": {
        ""tags"": [""DLL""],
        ""summary"": ""대상 TIA 버전 전환 (로드 전에만 가능)"",
        ""description"": ""AssemblyResolve 가 사용할 TIA 버전을 바꿉니다. **Openness 가 아직 로드되지 않은 경우에만** 성공합니다.\\n\\n한 번이라도 TIA API 를 호출해 Openness 가 로드된 뒤에는 409 를 반환합니다 — .NET 이 어셈블리를 언로드/교체할 수 없기 때문이며, 이때는 --tia= 인자로 프로세스를 재시작해야 합니다."",
        ""parameters"": [
          {""name"": ""version"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""대상 버전 (19 또는 20)""}
        ],
        ""responses"": {
          ""200"": {""description"": ""전환 완료""},
          ""400"": {""description"": ""지원하지 않는 버전""},
          ""404"": {""description"": ""해당 버전의 Openness 어셈블리 없음""},
          ""409"": {""description"": ""이미 다른 버전이 로드되어 전환 불가 - 프로세스 재시작 필요""}
        }
      }
    },
    ""/api/processes"": {
      ""get"": {
        ""tags"": [""Connection""],
        ""summary"": ""실행 중인 TIA Portal 프로세스 목록 조회"",
        ""description"": ""현재 PC에서 실행 중인 모든 TIA Portal 프로세스를 검색합니다. 각 프로세스의 ID와 열려있는 프로젝트 경로를 반환합니다."",
        ""responses"": {""200"": {""description"": ""TIA Portal 프로세스 목록""}}
      }
    },
    ""/api/connect"": {
      ""get"": {
        ""tags"": [""Connection""],
        ""summary"": ""TIA Portal 프로세스에 연결 (Attach)"",
        ""description"": ""지정된 TIA Portal 프로세스에 연결합니다. processId를 생략하면 첫 번째 프로세스에 자동 연결됩니다. 이 API를 호출해야 다른 모든 API를 사용할 수 있습니다."",
        ""parameters"": [{""name"": ""processId"", ""in"": ""query"", ""schema"": {""type"": ""integer""}, ""description"": ""연결할 TIA Portal 프로세스 ID (생략 시 첫 번째 프로세스에 자동 연결)""}],
        ""responses"": {""200"": {""description"": ""연결 성공""}, ""404"": {""description"": ""실행 중인 TIA Portal을 찾을 수 없음""}}
      }
    },
    ""/api/status"": {
      ""get"": {
        ""tags"": [""Connection""],
        ""summary"": ""현재 연결 상태 확인"",
        ""description"": ""API 서버가 TIA Portal에 연결되어 있는지, 열려있는 프로젝트가 몇 개인지 확인합니다."",
        ""responses"": {""200"": {""description"": ""연결 상태 정보""}}
      }
    },
    ""/api/projects"": {
      ""get"": {
        ""tags"": [""Project""],
        ""summary"": ""열려있는 프로젝트 목록 조회"",
        ""description"": ""현재 TIA Portal에서 열려있는 모든 프로젝트의 이름과 경로를 반환합니다."",
        ""responses"": {""200"": {""description"": ""프로젝트 목록""}}
      }
    },
    ""/api/devices"": {
      ""get"": {
        ""tags"": [""Project""],
        ""summary"": ""프로젝트의 모든 디바이스 목록 조회"",
        ""description"": ""프로젝트에 포함된 모든 디바이스(PLC, HMI, 드라이브 등)와 하위 아이템을 조회합니다. 반환된 PLC/HMI 아이템 이름을 다른 API의 plc 또는 hmi 파라미터에 사용하세요."",
        ""parameters"": [{""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""특정 프로젝트만 필터링 (생략 시 모든 프로젝트)""}],
        ""responses"": {""200"": {""description"": ""디바이스 목록 및 하위 아이템 정보""}}
      }
    },
    ""/api/portal/new"": {
      ""get"": {
        ""tags"": [""Setup""],
        ""summary"": ""새 TIA Portal 인스턴스 시작"",
        ""description"": ""새로운 TIA Portal 프로세스를 실행하고 자동으로 연결합니다. 기존 연결은 새 연결로 대체됩니다. WithUserInterface: UI 표시 (기본값), WithoutUserInterface: 백그라운드 실행 (자동화에 적합). TIA Portal 시작에 수십 초가 소요될 수 있습니다."",
        ""parameters"": [{""name"": ""mode"", ""in"": ""query"", ""schema"": {""type"": ""string"", ""enum"": [""WithUserInterface"", ""WithoutUserInterface""], ""default"": ""WithUserInterface""}, ""description"": ""실행 모드 - WithUserInterface(UI 표시) 또는 WithoutUserInterface(백그라운드)""}],
        ""responses"": {""200"": {""description"": ""새 TIA Portal이 시작되고 연결됨""}}
      }
    },
    ""/api/project/create"": {
      ""get"": {
        ""tags"": [""Setup""],
        ""summary"": ""새 프로젝트 생성"",
        ""description"": ""TIA Portal에 새 프로젝트를 생성합니다. 지정된 경로에 프로젝트 폴더가 생성됩니다."",
        ""parameters"": [
          {""name"": ""path"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""프로젝트를 저장할 디렉토리 경로 (예: C:\\\\Projects)""},
          {""name"": ""name"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""생성할 프로젝트 이름""}
        ],
        ""responses"": {""200"": {""description"": ""프로젝트 생성 성공""}, ""400"": {""description"": ""path 또는 name 파라미터 누락""}}
      }
    },
    ""/api/project/add-device"": {
      ""get"": {
        ""tags"": [""Setup""],
        ""summary"": ""프로젝트에 디바이스(PLC) 추가"",
        ""description"": ""프로젝트에 새 디바이스를 추가합니다. 기본값은 S7-1500 PLC입니다. Order Number 예시: S7-1517: OrderNumber:6ES7 517-3AP00-0AB0/V3.1"",
        ""parameters"": [
          {""name"": ""name"", ""in"": ""query"", ""schema"": {""type"": ""string"", ""default"": ""NewDevice""}, ""description"": ""디바이스 이름""},
          {""name"": ""type"", ""in"": ""query"", ""schema"": {""type"": ""string"", ""default"": ""System:Device.S71500""}, ""description"": ""디바이스 타입 식별자""},
          {""name"": ""order"", ""in"": ""query"", ""schema"": {""type"": ""string"", ""default"": ""OrderNumber:6ES7 517-3AP00-0AB0/V3.1""}, ""description"": ""주문번호 및 펌웨어 버전""},
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름 (생략 시 첫 번째 프로젝트)""}
        ],
        ""responses"": {""200"": {""description"": ""디바이스 추가 성공""}, ""404"": {""description"": ""프로젝트를 찾을 수 없음""}}
      }
    },
    ""/api/catalog/search"": {
      ""get"": {
        ""tags"": [""Catalog""],
        ""summary"": ""하드웨어 카탈로그 검색"",
        ""description"": ""TIA Portal 하드웨어 카탈로그에서 키워드로 검색합니다. 예: q=1FK2 (모터), q=S210 (드라이브)"",
        ""parameters"": [
          {""name"": ""q"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""검색어 (예: 1FK2, S210, 6ES7)""}
        ],
        ""responses"": {""200"": {""description"": ""카탈로그 항목 목록 (articleNumber, typeName, description, version 포함)""}}
      }
    },
    ""/api/catalog/compatible"": {
      ""get"": {
        ""tags"": [""Catalog""],
        ""summary"": ""특정 디바이스에 호환되는 모듈 검색"",
        ""description"": ""특정 디바이스의 TypeIdentifier를 기준으로 호환 가능한 모듈을 검색합니다. S210에 꽂을 수 있는 1FK2 모터 목록 등을 조회할 수 있습니다."",
        ""parameters"": [
          {""name"": ""q"", ""in"": ""query"", ""schema"": {""type"": ""string"", ""default"": """"}, ""description"": ""검색 필터 (빈 값이면 전체 호환 목록)""},
          {""name"": ""type"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""대상 디바이스의 TypeIdentifier (예: OrderNumber:6SL3210-5HE10-4xFx/V5.2.3/S210)""}
        ],
        ""responses"": {""200"": {""description"": ""호환 가능한 카탈로그 항목 목록""}}
      }
    },
    ""/api/device/items"": {
      ""get"": {
        ""tags"": [""Device Explorer""],
        ""summary"": ""디바이스의 전체 아이템 트리 조회"",
        ""description"": ""디바이스 내부의 모든 DeviceItem(모듈, 서브모듈, 인터페이스, 포트)을 재귀적으로 탐색합니다. SINAMICS S210 같은 드라이브의 내부 구조를 파악할 때 유용합니다."",
        ""parameters"": [
          {""name"": ""device"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""디바이스 이름 (예: S210_1)""},
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름 (생략 시 전체 검색)""}
        ],
        ""responses"": {""200"": {""description"": ""DeviceItem 트리 - name, type, classification, position, children 포함""}}
      }
    },
    ""/api/device/item-attributes"": {
      ""get"": {
        ""tags"": [""Device Explorer""],
        ""summary"": ""특정 DeviceItem의 모든 속성 읽기"",
        ""description"": ""DeviceItem의 읽기 가능한 모든 속성(이름 + 값)을 반환합니다. 슬래시(/)로 구분된 경로를 사용하여 하위 아이템을 지정합니다. 예: device=S210_1&item=S210/PROFINET interface"",
        ""parameters"": [
          {""name"": ""device"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""디바이스 이름""},
          {""name"": ""item"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""슬래시(/)로 구분된 DeviceItem 경로 (예: S210/PROFINET interface)""},
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름 (생략 시 전체 검색)""}
        ],
        ""responses"": {""200"": {""description"": ""속성 이름/값 쌍 목록""}}
      },
      ""put"": {
        ""tags"": [""Device Explorer""],
        ""summary"": ""DeviceItem 속성 수정 (CPU 프로퍼티 쓰기)"",
        ""description"": ""IEngineeringObject.SetAttribute를 사용하여 DeviceItem(CPU/모듈/인터페이스 등)의 속성 값을 변경합니다. 현재 값의 런타임 타입 또는 AttributeInfo.GetAttributeType()으로 타입을 추론하여 값을 자동 변환합니다. 읽기 전용 속성은 오류로 반환됩니다. 여러 속성을 한 번의 요청으로 일괄 수정할 수 있습니다."",
        ""requestBody"": {
          ""required"": true,
          ""content"": {
            ""application/json"": {
              ""schema"": {
                ""type"": ""object"",
                ""required"": [""device"", ""item"", ""attributes""],
                ""properties"": {
                  ""device"": {""type"": ""string"", ""description"": ""디바이스 이름""},
                  ""item"": {""type"": ""string"", ""description"": ""슬래시(/)로 구분된 DeviceItem 경로""},
                  ""project"": {""type"": ""string"", ""description"": ""프로젝트 이름 (생략 시 전체 검색)""},
                  ""attributes"": {
                    ""type"": ""array"",
                    ""description"": ""수정할 속성 목록"",
                    ""items"": {
                      ""type"": ""object"",
                      ""required"": [""name""],
                      ""properties"": {
                        ""name"": {""type"": ""string"", ""description"": ""속성 이름 (예: IpAddress, SubnetMask, DeviceNumber)""},
                        ""value"": {""description"": ""새 값 (문자열/숫자/불리언/enum 이름). 타입 자동 변환.""}
                      }
                    }
                  }
                },
                ""example"": {
                  ""device"": ""PLC_1"",
                  ""item"": ""PLC_1"",
                  ""attributes"": [
                    {""name"": ""IpAddress"", ""value"": ""192.168.0.10""},
                    {""name"": ""SubnetMask"", ""value"": ""255.255.255.0""}
                  ]
                }
              }
            }
          }
        },
        ""responses"": {
          ""200"": {""description"": ""수정 결과 목록 (각 속성별 성공/실패, 이전값/새값 포함)""},
          ""400"": {""description"": ""잘못된 요청 (device/item/attributes 누락)""},
          ""404"": {""description"": ""DeviceItem을 찾을 수 없음""}
        }
      }
    },
    ""/api/device/subitems"": {
      ""get"": {
        ""tags"": [""Device Explorer""],
        ""summary"": ""하위 아이템 + 전체 속성 덤프 (Deep Dump)"",
        ""description"": ""DeviceItem을 재귀적으로 탐색하면서 각 아이템의 모든 속성을 함께 반환합니다. 모터 선택 데이터, 드라이브 파라미터 등을 발견할 때 유용합니다. depth 파라미터로 탐색 깊이를 제어하세요."",
        ""parameters"": [
          {""name"": ""device"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""디바이스 이름""},
          {""name"": ""item"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""시작 DeviceItem 경로 (생략 시 디바이스 루트부터 시작)""},
          {""name"": ""depth"", ""in"": ""query"", ""schema"": {""type"": ""integer"", ""default"": 2}, ""description"": ""최대 재귀 깊이 (기본값: 2)""},
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름 (생략 시 전체 검색)""}
        ],
        ""responses"": {""200"": {""description"": ""전체 속성이 포함된 DeviceItem 트리""}}
      }
    },
    ""/api/device/export"": {
      ""get"": {
        ""tags"": [""Device Explorer""],
        ""summary"": ""디바이스 하드웨어 구성 Export (JSON ZIP)"",
        ""description"": ""프로젝트의 전체 하드웨어 구성(디바이스, 슬롯, 서브넷, IP 설정, IO 시스템, 토폴로지)을 JSON ZIP 파일로 내보냅니다. ZIP 안에는 manifest.json 과 hardware/*.json 이 들어 있어 외부 시스템에서 그대로 파싱해 구성을 복원할 수 있습니다."",
        ""parameters"": [
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름 (생략 시 첫 번째 프로젝트)""}
        ],
        ""responses"": {
          ""200"": {""description"": ""ZIP 파일 다운로드 (manifest.json + hardware/*.json)""},
          ""404"": {""description"": ""프로젝트를 찾을 수 없음""},
          ""500"": {""description"": ""Export 실패""}
        }
      }
    },
    ""/api/drive/objects"": {
      ""get"": {
        ""tags"": [""Drive""],
        ""summary"": ""디바이스의 DriveObject 목록 조회"",
        ""description"": ""지정된 디바이스에서 Startdrive를 통해 접근 가능한 모든 DriveObject를 나열합니다."",
        ""parameters"": [
          {""name"": ""device"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""디바이스 이름 (예: S210_1)""},
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름 (생략 시 전체 검색)""}
        ],
        ""responses"": {""200"": {""description"": ""DriveObject 목록 (파라미터 개수 포함)""}}
      }
    },
    ""/api/drive/parameters"": {
      ""get"": {
        ""tags"": [""Drive""],
        ""summary"": ""드라이브 파라미터 읽기"",
        ""description"": ""DriveObject에서 드라이브 파라미터를 읽습니다. 기본값: 모터 관련 파라미터 (p300~p311). 특정 파라미터: param=300. 범위 조회: range=300-320"",
        ""parameters"": [
          {""name"": ""device"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""디바이스 이름""},
          {""name"": ""do"", ""in"": ""query"", ""schema"": {""type"": ""integer""}, ""description"": ""DriveObject 번호 (기본값: 0)""},
          {""name"": ""param"", ""in"": ""query"", ""schema"": {""type"": ""integer""}, ""description"": ""특정 파라미터 번호 (예: 300)""},
          {""name"": ""range"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""파라미터 범위 (예: 300-320)""},
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름 (생략 시 전체 검색)""}
        ],
        ""responses"": {""200"": {""description"": ""드라이브 파라미터 값, 단위, Enum 목록""}}
      }
    },
    ""/api/drive/motor-config"": {
      ""get"": {
        ""tags"": [""Drive""],
        ""summary"": ""현재 모터 설정 조회"",
        ""description"": ""HardwareProjection을 통해 현재 모터 설정을 조회합니다. 필수(Required) 및 선택(Optional) ConfigurationEntry와 각 항목의 Enum 값 목록을 반환합니다."",
        ""parameters"": [
          {""name"": ""device"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""디바이스 이름""},
          {""name"": ""do"", ""in"": ""query"", ""schema"": {""type"": ""integer""}, ""description"": ""DriveObject 번호 (기본값: 0)""},
          {""name"": ""dds"", ""in"": ""query"", ""schema"": {""type"": ""integer""}, ""description"": ""Drive Data Set 번호 (기본값: 0)""},
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름 (생략 시 전체 검색)""}
        ],
        ""responses"": {""200"": {""description"": ""모터 설정 - 필수/선택 항목과 Enum 값 목록""}}
      }
    },
    ""/api/plc/create-unit"": {
      ""get"": {
        ""tags"": [""Import""],
        ""summary"": ""새 Software Unit 생성"",
        ""description"": ""PLC에 새로운 Software Unit을 생성합니다. Software Unit은 PLC 프로그램을 기능별로 모듈화하여 독립적으로 관리할 수 있게 해줍니다. namespace 파라미터를 지정하면 PlcUnit의 NamespacePreset 속성을 함께 설정합니다."",
        ""parameters"": [
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""},
          {""name"": ""name"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""생성할 Software Unit 이름""},
          {""name"": ""namespace"", ""in"": ""query"", ""required"": false, ""schema"": {""type"": ""string""}, ""description"": ""Software Unit 네임스페이스 (선택). 지정하지 않으면 기본 네임스페이스로 생성됩니다.""}
        ],
        ""responses"": {""200"": {""description"": ""Unit 생성 성공 - name, namespace, namespaceError 반환""}}
      }
    },
    ""/api/plc/techno-objects"": {
      ""get"": {
        ""tags"": [""PLC""],
        ""summary"": ""Technology Object 목록 조회"",
        ""description"": ""PlcSoftware.TechnologicalObjectGroup 의 모든 TO 를 user group 폴더까지 재귀 enumerate. TO 이름, 그룹 경로, system library element/version 반환."",
        ""parameters"": [{""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""}],
        ""responses"": {""200"": {""description"": ""count, technoObjects[] (name, group, ofSystemLibElement, ofSystemLibVersion)""}}
      }
    },
    ""/api/plc/techno-object/export"": {
      ""get"": {
        ""tags"": [""PLC""],
        ""summary"": ""단일 Technology Object XML Export"",
        ""description"": ""특정 TO 를 Simatic ML XML 파일로 export 다운로드. TechnologicalInstanceDB.Export(FileInfo, ExportOptions.WithDefaults) 호출. compile=true 옵션으로 export 전 자동 컴파일 → 'Inconsistent blocks ... cannot be exported' 에러 자동 해결."",
        ""parameters"": [
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}},
          {""name"": ""name"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""TO 이름 (그룹 경로 무관, 재귀 검색)""},
          {""name"": ""compile"", ""in"": ""query"", ""required"": false, ""schema"": {""type"": ""boolean"", ""default"": false}, ""description"": ""true 면 export 전 PLC 자동 컴파일 (inconsistent TO 자동 해결)""}
        ],
        ""responses"": {
          ""200"": {""description"": ""XML 파일 다운로드""},
          ""404"": {""description"": ""TO 찾을 수 없음""}
        }
      }
    },
    ""/api/plc/techno-objects/export/zip"": {
      ""get"": {
        ""tags"": [""PLC""],
        ""summary"": ""모든 Technology Object 를 ZIP 으로 Export"",
        ""description"": ""TechnologicalObjectGroup 전체 트리를 walk 하면서 각 TO 를 XML 로 export 한 후 user group 폴더 구조를 보존한 ZIP 으로 묶어 다운로드. compile=true 로 export 전 자동 컴파일 (Inconsistent TO 시 필수)."",
        ""parameters"": [
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}},
          {""name"": ""compile"", ""in"": ""query"", ""required"": false, ""schema"": {""type"": ""boolean"", ""default"": false}, ""description"": ""true 면 export 전 PLC 자동 컴파일""}
        ],
        ""responses"": {""200"": {""description"": ""ZIP 파일 다운로드 (TO XML + 폴더 구조)""}}
      }
    },
    ""/api/plc/techno-object/import"": {
      ""post"": {
        ""tags"": [""Import""],
        ""summary"": ""Technology Object XML Import (한 파일 = 여러 TO 가능)"",
        ""description"": ""TechnologicalInstanceDBComposition.Import(file, mode) 호출. Simatic ML XML 한 파일에 여러 TO 가 들어있을 수 있고 IList 반환. body 는 XML raw. mode 로 ImportOptions 선택 가능 (None / Override / SkipInactiveCultures / ActivateInactiveCultures)."",
        ""parameters"": [
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}},
          {""name"": ""group"", ""in"": ""query"", ""required"": false, ""schema"": {""type"": ""string""}, ""description"": ""target user group 경로 (생략 시 system root). 예: MyMotion/Axes""},
          {""name"": ""mode"", ""in"": ""query"", ""required"": false, ""schema"": {""type"": ""string"", ""enum"": [""Override"", ""None"", ""SkipInactiveCultures"", ""ActivateInactiveCultures""], ""default"": ""Override""}, ""description"": ""ImportOptions enum 값. Override (기존 덮어쓰기, 기본), None (충돌 시 실패)""}
        ],
        ""requestBody"": {
          ""required"": true,
          ""content"": {""application/xml"": {""schema"": {""type"": ""string"", ""format"": ""binary""}}}
        },
        ""responses"": {
          ""200"": {""description"": ""status=ok, importedCount, imported[]""},
          ""500"": {""description"": ""Import 실패 — error 메시지""}
        }
      }
    },
    ""/api/plc/techno-object/create-from-mastercopy"": {
      ""get"": {
        ""tags"": [""Import""],
        ""summary"": ""MasterCopy → Technology Object 생성"",
        ""description"": ""ProjectLibrary 의 MasterCopy 를 target user group 에 TO 로 복원. TechnologicalInstanceDBComposition.CreateFrom(MasterCopy)."",
        ""parameters"": [
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}},
          {""name"": ""mc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""MasterCopy 경로 (예: folder/mcName)""},
          {""name"": ""group"", ""in"": ""query"", ""required"": false, ""schema"": {""type"": ""string""}, ""description"": ""target user group 경로 (생략 시 system root)""}
        ],
        ""responses"": {""200"": {""description"": ""status=ok, createdName""}}
      }
    },
    ""/api/plc/prodiag/create-test"": {
      ""get"": {
        ""tags"": [""Import""],
        ""summary"": ""ProDiag FB 생성 시도 (검증용)"",
        ""description"": ""PlcBlockComposition.CreateFB() 에 ProgrammingLanguage.ProDiag / ProDiag_OB 를 전달해 ProDiag FB 직접 생성을 시도합니다. V20 에서 실제 허용되는지 미확인 — 거부되면 supervision XLSX import 경로 권장. 성공 시 생성된 FB 의 attribute / composition / service 를 모두 dump 해서 AssignedProDiagFB 같은 속성 위치 파악용으로 사용 가능."",
        ""parameters"": [
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""},
          {""name"": ""blockName"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""생성할 ProDiag FB 이름 (예: My_ProDiag_FB)""},
          {""name"": ""number"", ""in"": ""query"", ""required"": false, ""schema"": {""type"": ""integer""}, ""description"": ""블록 번호 (0 또는 미지정 시 auto-number)""},
          {""name"": ""lang"", ""in"": ""query"", ""required"": false, ""schema"": {""type"": ""string"", ""enum"": [""ProDiag"", ""ProDiag_OB""], ""default"": ""ProDiag""}, ""description"": ""ProgrammingLanguage enum 값 (ProDiag = FB, ProDiag_OB = OB)""}
        ],
        ""responses"": {
          ""200"": {""description"": ""성공 시: status, blockName, requestedLanguage, blockType, attributes(전체 dump), compositions, services""},
          ""500"": {""description"": ""실패 시: status=create_failed, error, innerError""}
        }
      }
    },
    ""/api/plc/prodiag/add-supervision-test"": {
      ""get"": {
        ""tags"": [""Import""],
        ""summary"": ""ProDiag FB 의 Supervisions 에 Supervision 추가 시도 (검증용)"",
        ""description"": ""IEngineeringObject.Create(\""Supervisions\"", typeof(Supervision), ...) 로 ProDiag FB 의 Supervisions composition 에 Supervision 객체를 추가 시도합니다. 시도한 모든 방법의 결과(성공/실패+에러)와 생성된 Supervision 의 attribute/composition 을 dump 합니다. supervised tag / DB 참조 attribute 위치 파악용."",
        ""parameters"": [
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""},
          {""name"": ""block"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""대상 FB 이름 (ProDiag 또는 일반 FB)""},
          {""name"": ""name"", ""in"": ""query"", ""required"": false, ""schema"": {""type"": ""string""}, ""description"": ""생성할 Supervision 이름 (미지정 시 Supv_<HHmmss>)""}
        ],
        ""responses"": {
          ""200"": {""description"": ""attempts[] (각 Create 시도 결과), createdSupervision (성공 시 attribute dump), creationInfosForSupervisions (가능한 type 목록), supervisionsCount""}
        }
      }
    },
    ""/api/plc/blocks"": {
      ""get"": {
        ""tags"": [""PLC""],
        ""summary"": ""PLC 블록 전체 목록 조회 (재귀)"",
        ""description"": ""PLC의 모든 블록(OB, FB, FC, DB)을 그룹 구조를 포함하여 재귀적으로 조회합니다. 각 블록의 이름, 번호, 프로그래밍 언어, 타입, 그룹 경로를 반환합니다."",
        ""parameters"": [{""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름 (예: PLC_1, MyPlc)""}],
        ""responses"": {""200"": {""description"": ""블록 목록 - name, number, language, type, group 포함""}}
      }
    },
    ""/api/plc/block-detail"": {
      ""get"": {
        ""tags"": [""PLC""],
        ""summary"": ""특정 블록 상세 정보 조회"",
        ""description"": ""특정 PLC 블록의 상세 정보를 조회합니다. 블록 이름, 번호, 프로그래밍 언어, 타입, 수정일시, 인터페이스(Input/Output/InOut/Static/Temp) 등을 반환합니다."",
        ""parameters"": [
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""},
          {""name"": ""name"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""블록 이름 (예: Main, FB_Motor)""}
        ],
        ""responses"": {""200"": {""description"": ""블록 상세 정보""}}
      }
    },
    ""/api/plc/types"": {
      ""get"": {
        ""tags"": [""PLC""],
        ""summary"": ""PLC 데이터 타입(UDT) 전체 목록 조회"",
        ""description"": ""PLC에 정의된 모든 사용자 정의 데이터 타입(UDT)을 조회합니다."",
        ""parameters"": [{""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""}],
        ""responses"": {""200"": {""description"": ""UDT 목록""}}
      }
    },
    ""/api/plc/tags"": {
      ""get"": {
        ""tags"": [""PLC""],
        ""summary"": ""PLC 태그 테이블 전체 목록 조회"",
        ""description"": ""PLC의 모든 태그 테이블과 각 테이블에 포함된 태그(이름, 데이터 타입, 주소, 코멘트)를 조회합니다. 하위 그룹의 태그 테이블도 재귀적으로 포함됩니다."",
        ""parameters"": [{""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""}],
        ""responses"": {""200"": {""description"": ""태그 테이블 목록 (태그 상세 포함)""}}
      }
    },
    ""/api/plc/units"": {
      ""get"": {
        ""tags"": [""PLC""],
        ""summary"": ""Software Unit 전체 목록 조회"",
        ""description"": ""PLC에 생성된 모든 Software Unit을 조회합니다. Software Unit은 PLC 프로그램을 모듈화하여 관리하는 단위입니다."",
        ""parameters"": [{""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""}],
        ""responses"": {""200"": {""description"": ""Software Unit 목록 (블록/타입/태그 개수 포함)""}}
      }
    },
    ""/api/plc/units/{unitName}/blocks"": {
      ""get"": {
        ""tags"": [""PLC""],
        ""summary"": ""특정 Software Unit의 블록 목록 조회"",
        ""description"": ""지정된 Software Unit에 포함된 모든 블록을 조회합니다."",
        ""parameters"": [
          {""name"": ""unitName"", ""in"": ""path"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""Software Unit 이름""},
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""}
        ],
        ""responses"": {""200"": {""description"": ""해당 Unit의 블록 목록""}}
      }
    },
    ""/api/plc/units/{unitName}/types"": {
      ""get"": {
        ""tags"": [""PLC""],
        ""summary"": ""특정 Software Unit의 데이터 타입(UDT) 목록 조회"",
        ""description"": ""지정된 Software Unit에 정의된 모든 사용자 정의 데이터 타입을 조회합니다."",
        ""parameters"": [
          {""name"": ""unitName"", ""in"": ""path"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""Software Unit 이름""},
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""}
        ],
        ""responses"": {""200"": {""description"": ""해당 Unit의 UDT 목록""}}
      }
    },
    ""/api/plc/units/{unitName}/tags"": {
      ""get"": {
        ""tags"": [""PLC""],
        ""summary"": ""특정 Software Unit의 태그 테이블 목록 조회"",
        ""description"": ""지정된 Software Unit에 포함된 모든 태그 테이블과 태그를 조회합니다."",
        ""parameters"": [
          {""name"": ""unitName"", ""in"": ""path"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""Software Unit 이름""},
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""}
        ],
        ""responses"": {""200"": {""description"": ""해당 Unit의 태그 테이블 목록""}}
      }
    },
    ""/api/plc/export/block"": {
      ""get"": {
        ""tags"": [""Export""],
        ""summary"": ""단일 블록을 소스 파일로 다운로드"",
        ""description"": ""특정 PLC 블록을 소스 코드 파일로 다운로드합니다. SCL 블록은 .scl, DB 블록은 .db, STL 블록은 .awl 형식으로 내보냅니다. 주의: LAD/FBD 블록은 이 API로 내보낼 수 없습니다. /api/plc/export/xml을 사용하세요."",
        ""parameters"": [
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""},
          {""name"": ""name"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""블록 이름 (예: Main, FB_Motor)""},
          {""name"": ""unit"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""Software Unit 이름 (생략 시 루트 블록에서 검색)""}
        ],
        ""responses"": {""200"": {""description"": ""소스 파일 다운로드 (.scl / .db / .awl)""}}
      }
    },
    ""/api/plc/units/{unitName}/export"": {
      ""get"": {
        ""tags"": [""Export""],
        ""summary"": ""Software Unit 전체를 ZIP으로 다운로드 (소스 파일)"",
        ""description"": ""Software Unit의 모든 블록, 타입, 태그 테이블을 ZIP 파일로 다운로드합니다. ZIP 내부 폴더: scl/, db/, stl/, udt/, tag_tables/. 주의: LAD/FBD 블록은 포함되지 않습니다. 전체 블록이 필요하면 /export-xml을 사용하세요."",
        ""parameters"": [
          {""name"": ""unitName"", ""in"": ""path"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""Software Unit 이름""},
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""}
        ],
        ""responses"": {""200"": {""description"": ""ZIP 파일 다운로드""}}
      }
    },
    ""/api/plc/tree"": {
      ""get"": {
        ""tags"": [""PLC""],
        ""summary"": ""CPU 폴더 트리 조회 (export 가능한 XML 목록)"",
        ""description"": ""PLC 의 PLC data types / Program blocks / PLC tags / Software units 를 TIA 프로젝트 트리와 같은 계층으로 반환합니다.\n\n각 leaf 의 `path` 는 **ZIP 엔트리 경로이자 선택 ID** 입니다. 그 값을 그대로 모아 POST /api/plc/export/selected 의 body 에 넣으면 해당 XML 만 받습니다.\n\nleaf 의 `info` 에는 블록 타입/번호와 `consistent` 가 담깁니다 — `consistent:false` 인 블록은 export 가 실패하므로 ?compile=true 로 먼저 컴파일하세요.\n\n브라우저 UI 는 http://localhost:8099/explorer 에 있습니다."",
        ""parameters"": [
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""}
        ],
        ""responses"": {""200"": {""description"": ""{ plc, root, leafCount, tree[] } — folder 노드는 children, leaf 노드는 path 를 가집니다""}}
      }
    },
    ""/api/plc/export/selected"": {
      ""post"": {
        ""tags"": [""Export""],
        ""summary"": ""선택한 XML 만 ZIP 으로 다운로드"",
        ""description"": ""/api/plc/tree 가 돌려준 leaf `path` 배열을 받아 그 XML 만 내보냅니다. 경로 규칙과 export 순서(타입 → DB → 태그 → 코드블록)는 /api/plc/export/xml 과 동일하므로, 받은 ZIP 을 /api/plc/import/xml 로 그대로 되돌릴 수 있습니다.\n\nZIP 안 `_export_report.json` 에 requested / success / failed / notFound 가 들어갑니다. notFound 는 요청했지만 산출되지 않은 경로입니다(이름 변경 또는 export 실패)."",
        ""parameters"": [
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""},
          {""name"": ""compile"", ""in"": ""query"", ""schema"": {""type"": ""boolean"", ""default"": false}, ""description"": ""export 전에 software compile 1회 실행 (inconsistent 블록 자동 해결 시도)""}
        ],
        ""requestBody"": {
          ""required"": true,
          ""content"": {""application/json"": {""schema"": {
            ""type"": ""object"",
            ""required"": [""paths""],
            ""properties"": {""paths"": {""type"": ""array"", ""items"": {""type"": ""string""}, ""description"": ""tree 의 leaf path 배열""}}
          }, ""example"": {""paths"": [""PLC_1/Program blocks/Main.xml"", ""PLC_1/PLC data types/MyUdt.xml""]}}}
        },
        ""responses"": {
          ""200"": {""description"": ""ZIP 파일 다운로드 (선택분만)""},
          ""400"": {""description"": ""paths 누락 또는 JSON 파싱 실패""}
        }
      }
    },
    ""/api/plc/export/xml"": {
      ""get"": {
        ""tags"": [""Export""],
        ""summary"": ""PLC 전체를 XML ZIP으로 다운로드 (LAD/FBD 포함)"",
        ""description"": ""PLC의 모든 블록을 SimaticML XML 형식으로 내보냅니다. LAD/FBD 블록을 포함한 모든 블록 타입을 지원합니다. Export 범위: all(전체, 기본값), program_blocks(루트만), software_units(유닛만). 이 ZIP을 /api/plc/import/xml로 다시 Import할 수 있습니다."",
        ""parameters"": [
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""},
          {""name"": ""root"", ""in"": ""query"", ""schema"": {""type"": ""string"", ""enum"": [""all"", ""program_blocks"", ""software_units""], ""default"": ""all""}, ""description"": ""Export 범위: all(전체), program_blocks(루트만), software_units(유닛만)""}
        ],
        ""responses"": {""200"": {""description"": ""ZIP 파일 다운로드 (XML 형식)""}}
      }
    },
    ""/api/plc/units/{unitName}/export-xml"": {
      ""get"": {
        ""tags"": [""Export""],
        ""summary"": ""특정 Software Unit을 XML ZIP으로 다운로드 (LAD/FBD 포함)"",
        ""description"": ""특정 Software Unit의 모든 블록, 타입, 태그를 SimaticML XML 형식의 ZIP으로 내보냅니다. LAD/FBD를 포함한 모든 블록 타입을 지원합니다."",
        ""parameters"": [
          {""name"": ""unitName"", ""in"": ""path"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""Software Unit 이름""},
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""}
        ],
        ""responses"": {""200"": {""description"": ""ZIP 파일 다운로드 (XML 형식)""}}
      }
    },
    ""/api/plc/import/xml"": {
      ""get"": {
        ""tags"": [""Import""],
        ""summary"": ""로컬 XML 파일 또는 ZIP을 Import"",
        ""description"": ""로컬 파일 시스템의 XML 또는 ZIP을 PLC에 Import합니다. 단일 XML: 블록/타입/태그를 자동 감지. ZIP: /export/xml 구조를 인식하며 타입->태그->블록 순서로 의존성을 고려하여 처리. Software Unit과 블록 그룹은 자동 생성됩니다."",
        ""parameters"": [
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""},
          {""name"": ""file"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""로컬 파일 경로 (.xml 또는 .zip)""}
        ],
        ""responses"": {""200"": {""description"": ""Import 결과 - 파일별 성공/실패 상태""}, ""400"": {""description"": ""파일 경로 누락 또는 잘못된 확장자""}, ""500"": {""description"": ""TIA Portal Import 오류""}}
      }
    },
    ""/api/plc/import/upload"": {
      ""post"": {
        ""tags"": [""Import""],
        ""summary"": ""XML 파일 업로드를 통한 Import"",
        ""description"": ""HTTP 요청 본문에 XML 내용을 담아 직접 업로드합니다. target으로 Import 대상을 지정: blocks(블록), tags(태그), types(UDT). unit을 지정하면 해당 Software Unit에, 생략하면 PLC 루트에 Import됩니다."",
        ""parameters"": [
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름""},
          {""name"": ""target"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string"", ""enum"": [""blocks"", ""tags"", ""types""]}, ""description"": ""Import 대상: blocks(블록), tags(태그 테이블), types(UDT)""},
          {""name"": ""unit"", ""in"": ""query"", ""required"": false, ""schema"": {""type"": ""string""}, ""description"": ""Software Unit 이름 (생략 시 PLC 루트에 Import)""}
        ],
        ""requestBody"": {
          ""required"": true,
          ""description"": ""SimaticML XML 형식의 파일 내용"",
          ""content"": {
            ""application/xml"": {
              ""schema"": {""type"": ""string"", ""format"": ""binary""},
              ""example"": ""<?xml version='1.0' encoding='utf-8'?><Document>...</Document>""
            }
          }
        },
        ""responses"": {
          ""200"": {""description"": ""Import 성공 - 블록 종류와 Unit 정보 반환""},
          ""400"": {""description"": ""요청 본문이 비어있거나 필수 파라미터 누락""},
          ""404"": {""description"": ""지정된 Software Unit을 찾을 수 없음""},
          ""500"": {""description"": ""TIA Portal Import 오류""}
        }
      }
    },
    ""/api/plc/compile"": {
      ""post"": {
        ""tags"": [""Compile""],
        ""summary"": ""PLC 컴파일 (소프트웨어/하드웨어)"",
        ""description"": ""PLC 소프트웨어 또는 하드웨어를 컴파일하여 오류와 경고를 확인합니다.\\n\\n- **software** (기본값): PLC 프로그램 전체를 컴파일합니다. 블록 문법 오류, 미해결 참조, 타입 불일치 등을 검출합니다.\\n- **hardware**: 디바이스 하드웨어 구성을 검증합니다. IO 주소 충돌, 모듈 호환성, 슬롯 설정 오류 등을 검출합니다.\\n\\nImport 후 자동 검증이나 CI/CD 파이프라인에서의 품질 체크에 활용할 수 있습니다."",
        ""parameters"": [
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC 디바이스 아이템 이름 (예: PLC_1)""},
          {""name"": ""mode"", ""in"": ""query"", ""required"": false, ""schema"": {""type"": ""string"", ""enum"": [""software"", ""hardware""], ""default"": ""software""}, ""description"": ""컴파일 모드: software(PLC 프로그램), hardware(하드웨어 구성)""}
        ],
        ""responses"": {
          ""200"": {""description"": ""컴파일 결과 - state(Success/Warning/Error), 오류/경고 수, 메시지 목록""},
          ""400"": {""description"": ""컴파일을 지원하지 않는 대상""},
          ""404"": {""description"": ""PLC 또는 디바이스를 찾을 수 없음""},
          ""500"": {""description"": ""컴파일 실행 중 오류""}
        }
      }
    },
    ""/api/hmi/tags"": {
      ""get"": {
        ""tags"": [""HMI""],
        ""summary"": ""HMI 태그 테이블 목록 조회"",
        ""description"": ""HMI 디바이스의 모든 태그 테이블을 조회합니다. 시스템 폴더와 사용자 폴더의 태그 테이블을 재귀적으로 탐색합니다.\\n\\n각 항목에 폴더 경로(path)와 태그 개수(tagCount)가 포함됩니다. ?detailed=true 로 호출하면 각 테이블에 포함된 모든 태그(name, dataType, connection, address, length, accessMode, comment)도 함께 반환합니다."",
        ""parameters"": [
          {""name"": ""hmi"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""HMI 디바이스 아이템 이름 (예: HMI_1)""},
          {""name"": ""detailed"", ""in"": ""query"", ""schema"": {""type"": ""boolean"", ""default"": false}, ""description"": ""true 로 설정하면 각 테이블의 태그 상세 정보를 함께 반환""}
        ],
        ""responses"": {""200"": {""description"": ""HMI 태그 테이블 목록 (detailed=true 시 태그 상세 포함)""}}
      }
    },
    ""/api/hmi/tag-table"": {
      ""get"": {
        ""tags"": [""HMI""],
        ""summary"": ""HMI 태그 테이블 상세 조회 (개별 태그 목록)"",
        ""description"": ""특정 HMI 태그 테이블에 정의된 모든 태그를 조회합니다.\\n\\n반환 필드: name, dataType, connection, address, length, accessMode, comment.\\n\\nfolder 파라미터를 비우면 시스템 폴더 직속 테이블에서 먼저 찾고, 없으면 사용자 폴더 전체를 재귀 탐색합니다."",
        ""parameters"": [
          {""name"": ""hmi"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""HMI 디바이스 아이템 이름""},
          {""name"": ""table"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""태그 테이블 이름""},
          {""name"": ""folder"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""사용자 폴더 경로 (예: SubA/SubB) — 생략 시 자동 탐색""}
        ],
        ""responses"": {
          ""200"": {""description"": ""태그 목록""},
          ""400"": {""description"": ""table 파라미터 누락""},
          ""404"": {""description"": ""HMI 또는 테이블을 찾을 수 없음""}
        }
      }
    },
    ""/api/hmi/tag-table/export"": {
      ""get"": {
        ""tags"": [""HMI""],
        ""summary"": ""HMI 태그 테이블 XML 내보내기"",
        ""description"": ""특정 HMI 태그 테이블을 XML 파일로 내보냅니다. ExportOptions.WithDefaults 옵션 사용."",
        ""parameters"": [
          {""name"": ""hmi"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""HMI 디바이스 아이템 이름""},
          {""name"": ""table"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""태그 테이블 이름""},
          {""name"": ""folder"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""사용자 폴더 경로 (생략 시 자동 탐색)""}
        ],
        ""responses"": {
          ""200"": {""description"": ""태그 테이블 XML 다운로드""},
          ""400"": {""description"": ""table 파라미터 누락""},
          ""404"": {""description"": ""테이블을 찾을 수 없음""},
          ""500"": {""description"": ""Export 실패""}
        }
      }
    },
    ""/api/hmi/tag-table/import"": {
      ""post"": {
        ""tags"": [""HMI""],
        ""summary"": ""HMI 태그 테이블 XML 가져오기"",
        ""description"": ""HMI 태그 테이블 XML을 POST body로 업로드하여 가져옵니다. ImportOptions.Override로 동일 이름 테이블을 덮어씁니다.\\n\\nfolder 파라미터로 대상 사용자 폴더를 지정할 수 있으며, 폴더가 없으면 자동 생성됩니다."",
        ""parameters"": [
          {""name"": ""hmi"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""HMI 디바이스 아이템 이름""},
          {""name"": ""folder"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""대상 사용자 폴더 경로 (예: SubA/SubB) — 없으면 자동 생성, 생략 시 시스템 폴더 직속에 import""}
        ],
        ""requestBody"": {
          ""description"": ""태그 테이블 XML 파일 내용"",
          ""required"": true,
          ""content"": {
            ""application/octet-stream"": {""schema"": {""type"": ""string"", ""format"": ""binary""}}
          }
        },
        ""responses"": {
          ""200"": {""description"": ""태그 테이블 import 성공""},
          ""400"": {""description"": ""요청 body가 비어있음""},
          ""404"": {""description"": ""HMI 디바이스를 찾을 수 없음""},
          ""500"": {""description"": ""Import 실패""}
        }
      }
    },
    ""/api/hmi/tags/export/zip"": {
      ""get"": {
        ""tags"": [""HMI""],
        ""summary"": ""HMI 태그 테이블 전체 ZIP 내보내기"",
        ""description"": ""HMI 디바이스의 모든 태그 테이블(시스템 + 사용자 폴더)을 XML로 내보내어 하나의 ZIP 파일로 다운로드합니다.\\n\\nZIP 내부 구조: HmiTags/ 아래 사용자 폴더 구조가 그대로 유지되며 각 태그 테이블이 .xml 파일로 저장됩니다.\\n\\nX-Export-Success / X-Export-Failed 헤더에 성공/실패 개수가 포함됩니다."",
        ""parameters"": [{""name"": ""hmi"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""HMI 디바이스 아이템 이름""}],
        ""responses"": {
          ""200"": {""description"": ""태그 테이블 XML이 포함된 ZIP 다운로드""},
          ""400"": {""description"": ""hmi 파라미터 누락""},
          ""404"": {""description"": ""HMI 디바이스를 찾을 수 없음""},
          ""500"": {""description"": ""ZIP export 실패""}
        }
      }
    },
    ""/api/hmi/textlists"": {
      ""get"": {
        ""tags"": [""HMI""],
        ""summary"": ""HMI 텍스트 리스트 목록 조회"",
        ""description"": ""HMI 디바이스에 정의된 모든 텍스트 리스트를 조회합니다. 텍스트 리스트는 HMI 화면에서 상태값을 사용자 친화적인 텍스트로 표시할 때 사용됩니다."",
        ""parameters"": [{""name"": ""hmi"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""HMI 디바이스 아이템 이름 (예: HMI_1)""}],
        ""responses"": {""200"": {""description"": ""HMI 텍스트 리스트 목록""}}
      }
    },
    ""/api/hmi/screens"": {
      ""get"": {
        ""tags"": [""HMI""],
        ""summary"": ""HMI 스크린 목록 조회 (Comfort/Advanced + Unified)"",
        ""description"": ""HMI 디바이스의 모든 스크린, 팝업, 템플릿 목록을 조회합니다. Comfort/Advanced 는 풀 트리, Unified 는 root screens + ScreenGroups 재귀 enumerate (Unified 는 Export 불가, 메타만)."",
        ""parameters"": [{""name"": ""hmi"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""HMI 디바이스 아이템 이름 (예: HMI_1, MTP)""}],
        ""responses"": {
          ""200"": {""description"": ""스크린/팝업/템플릿 목록 및 개수 (Unified 면 rootScreens + screenGroups 트리)""},
          ""400"": {""description"": ""hmi 파라미터 누락""},
          ""404"": {""description"": ""HMI 디바이스를 찾을 수 없음""}
        }
      }
    },
    ""/api/hmi/screen/create-mastercopy"": {
      ""get"": {
        ""tags"": [""HMI""],
        ""summary"": ""Unified HMI 스크린 → MasterCopy 생성 시도 (cross-project 1단계, V20 검증용)"",
        ""description"": ""Unified HMI 의 특정 screen 을 IMasterCopySource 로 캐스팅해서 ProjectLibrary 의 MasterCopy 로 생성을 시도합니다. V20 Openness 가 HmiScreen 을 library source 로 허용하는지 미확인 — 성공 시 cross-project 이전의 1단계. 거부되면 TIA Portal GUI 의 'Add to library' 수동 작업 필요."",
        ""parameters"": [
          {""name"": ""hmi"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""Unified HMI 디바이스 이름 (예: MTP)""},
          {""name"": ""screen"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""screen 경로 (예: 400_Alarm 또는 400_Alarm/401_Alarm History)""},
          {""name"": ""libPath"", ""in"": ""query"", ""required"": false, ""schema"": {""type"": ""string""}, ""description"": ""ProjectLibrary 내 폴더 경로 (생략 시 루트)""}
        ],
        ""responses"": {
          ""200"": {""description"": ""status=ok, masterCopyName, sourceScreen""},
          ""500"": {""description"": ""status=create_failed, error, hint (V20 한계)""}
        }
      }
    },
    ""/api/hmi/screen/create-from-mastercopy"": {
      ""post"": {
        ""tags"": [""HMI""],
        ""summary"": ""MasterCopy → Unified HMI 스크린 복원 시도 (Project Library / Global Library 지원)"",
        ""description"": ""MasterCopy 를 target Unified HMI 의 Screens composition 으로 복원합니다. library 지정 시 Global Library, 미지정 시 Project Library 에서 검색. V20 Openness 의 HmiScreenComposition 에 CreateFrom(MasterCopy) 메서드가 없어 reflection 으로 여러 시도 후 결과 보고."",
        ""parameters"": [
          {""name"": ""hmi"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""Target Unified HMI 디바이스 이름""},
          {""name"": ""masterCopy"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""MasterCopy 경로 (예: 00_Screen LayOut 또는 SubFolder/Screen400)""},
          {""name"": ""library"", ""in"": ""query"", ""required"": false, ""schema"": {""type"": ""string""}, ""description"": ""Global Library 이름 (예: MTPScreen). 생략 시 Project Library 사용""},
          {""name"": ""groupPath"", ""in"": ""query"", ""required"": false, ""schema"": {""type"": ""string""}, ""description"": ""target ScreenGroup 경로 (생략 시 root Screens)""}
        ],
        ""responses"": {
          ""200"": {""description"": ""status=ok, createdScreen, method""},
          ""500"": {""description"": ""status=all_attempts_failed, attempts[], hint""}
        }
      }
    },
    ""/api/hmi/screen-export"": {
      ""get"": {
        ""tags"": [""HMI""],
        ""summary"": ""HMI 스크린 XML 내보내기"",
        ""description"": ""특정 HMI 스크린(또는 팝업/템플릿)을 XML 파일로 내보냅니다. ExportOptions.WithDefaults 옵션으로 export됩니다."",
        ""parameters"": [
          {""name"": ""hmi"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""HMI 디바이스 아이템 이름 (예: HMI_1)""},
          {""name"": ""name"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""스크린 이름""},
          {""name"": ""kind"", ""in"": ""query"", ""schema"": {""type"": ""string"", ""enum"": [""screen"", ""popup"", ""template""], ""default"": ""screen""}, ""description"": ""스크린 종류 (기본값: screen)""}
        ],
        ""responses"": {
          ""200"": {""description"": ""스크린 XML 파일 다운로드""},
          ""400"": {""description"": ""name 파라미터 누락""},
          ""404"": {""description"": ""스크린을 찾을 수 없음""},
          ""500"": {""description"": ""스크린 export 실패""}
        }
      }
    },
    ""/api/hmi/screen-import"": {
      ""post"": {
        ""tags"": [""HMI""],
        ""summary"": ""HMI 스크린 XML 가져오기"",
        ""description"": ""HMI 스크린(또는 팝업/템플릿) XML을 POST body로 업로드하여 가져옵니다. ImportOptions.Override로 동일 이름 스크린을 덮어씁니다.\\n\\nscreen 종류의 경우 folder 파라미터로 대상 폴더를 지정할 수 있습니다."",
        ""parameters"": [
          {""name"": ""hmi"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""HMI 디바이스 아이템 이름 (예: HMI_1)""},
          {""name"": ""kind"", ""in"": ""query"", ""schema"": {""type"": ""string"", ""enum"": [""screen"", ""popup"", ""template""], ""default"": ""screen""}, ""description"": ""스크린 종류 (기본값: screen)""},
          {""name"": ""folder"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""대상 폴더 경로 (kind=screen일 때만 적용, 예: SubFolder1/SubFolder2)""}
        ],
        ""requestBody"": {
          ""description"": ""스크린 XML 파일 내용"",
          ""required"": true,
          ""content"": {
            ""application/octet-stream"": {
              ""schema"": {""type"": ""string"", ""format"": ""binary""}
            }
          }
        },
        ""responses"": {
          ""200"": {""description"": ""스크린 import 성공""},
          ""400"": {""description"": ""요청 body가 비어있음""},
          ""404"": {""description"": ""HMI 디바이스를 찾을 수 없음""},
          ""500"": {""description"": ""스크린 import 실패""}
        }
      }
    },
    ""/api/hmi/screens/export/zip"": {
      ""get"": {
        ""tags"": [""HMI""],
        ""summary"": ""HMI 스크린 전체 ZIP 내보내기"",
        ""description"": ""HMI 디바이스의 모든 스크린, 팝업, 템플릿을 XML로 내보내어 하나의 ZIP 파일로 다운로드합니다.\\n\\nZIP 내부 구조:\\n- Screens/ — 일반 스크린\\n- Popups/ — 팝업 스크린\\n- Templates/ — 스크린 템플릿\\n\\n각 폴더 하위에 사용자 폴더 구조가 그대로 유지됩니다."",
        ""parameters"": [{""name"": ""hmi"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""HMI 디바이스 아이템 이름 (예: HMI_1)""}],
        ""responses"": {
          ""200"": {""description"": ""스크린 XML이 포함된 ZIP 파일 다운로드 (X-Export-Success, X-Export-Failed 헤더에 성공/실패 개수 포함)""},
          ""400"": {""description"": ""hmi 파라미터 누락""},
          ""404"": {""description"": ""HMI 디바이스를 찾을 수 없음""},
          ""500"": {""description"": ""ZIP export 실패""}
        }
      }
    },
    ""/api/hmi/connections/list"": {
      ""get"": {
        ""tags"": [""HMI""],
        ""summary"": ""HMI Connection 전체 목록 + 상세 attribute"",
        ""description"": ""HMI 의 모든 Connection 을 attribute 까지 포함해서 반환합니다.\\n각 connection 의 IEngineeringObject.GetAttributeInfos() 결과를 모두 dump."",
        ""parameters"": [{""name"": ""hmi"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""HMI 디바이스 아이템 이름""}],
        ""responses"": {
          ""200"": {""description"": ""연결 목록 + attribute""},
          ""404"": {""description"": ""HMI 를 찾을 수 없음""}
        }
      }
    },
    ""/api/hmi/connection/detail"": {
      ""get"": {
        ""tags"": [""HMI""],
        ""summary"": ""HMI Connection 단일 상세 조회"",
        ""description"": ""ConnectionComposition.Find(name) 으로 단일 connection 을 찾고 모든 attribute 를 반환합니다."",
        ""parameters"": [
          {""name"": ""hmi"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}},
          {""name"": ""name"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""Connection 이름 (예: HMI_Connection_1)""}
        ],
        ""responses"": {
          ""200"": {""description"": ""attribute 사전""},
          ""400"": {""description"": ""name 파라미터 누락""},
          ""404"": {""description"": ""Connection 을 찾을 수 없음""}
        }
      }
    },
    ""/api/hmi/connection/export"": {
      ""get"": {
        ""tags"": [""HMI""],
        ""summary"": ""HMI Connection XML 내보내기"",
        ""description"": ""특정 connection 을 XML 파일로 export. Connection.Export(file, ExportOptions.WithDefaults) 사용."",
        ""parameters"": [
          {""name"": ""hmi"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}},
          {""name"": ""name"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""Connection 이름""}
        ],
        ""responses"": {
          ""200"": {""description"": ""Connection XML 다운로드""},
          ""400"": {""description"": ""name 파라미터 누락""},
          ""404"": {""description"": ""Connection 을 찾을 수 없음""},
          ""500"": {""description"": ""Export 실패""}
        }
      }
    },
    ""/api/hmi/connection/import"": {
      ""post"": {
        ""tags"": [""HMI""],
        ""summary"": ""HMI Connection XML 가져오기"",
        ""description"": ""ConnectionComposition.Import(file, ImportOptions.Override) 로 XML 을 import. 동일 이름 connection 은 덮어쓰기."",
        ""parameters"": [{""name"": ""hmi"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}}],
        ""requestBody"": {
          ""description"": ""Connection XML 내용"",
          ""required"": true,
          ""content"": {
            ""application/octet-stream"": {""schema"": {""type"": ""string"", ""format"": ""binary""}}
          }
        },
        ""responses"": {
          ""200"": {""description"": ""Import 성공""},
          ""400"": {""description"": ""body 비어있음""},
          ""500"": {""description"": ""Import 실패""}
        }
      }
    },
    ""/api/hmi/connections/export/zip"": {
      ""get"": {
        ""tags"": [""HMI""],
        ""summary"": ""HMI Connection 전체 ZIP 내보내기"",
        ""description"": ""HMI 디바이스의 모든 connection 을 XML 로 export 하여 하나의 ZIP 으로 묶어 다운로드합니다.\\nZIP 내부 구조: Connections/<연결이름>.xml\\n\\nX-Export-Success / X-Export-Failed 헤더에 성공/실패 개수 포함."",
        ""parameters"": [{""name"": ""hmi"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}}],
        ""responses"": {
          ""200"": {""description"": ""Connection XML ZIP 다운로드""},
          ""500"": {""description"": ""ZIP export 실패""}
        }
      }
    },
    ""/api/hmi/network-connect"": {
      ""post"": {
        ""tags"": [""HMI""],
        ""summary"": ""[Network View 동작] HMI ↔ CPU 통합 연결 (subnet + connection)"",
        ""description"": ""TIA Portal 의 'Devices & Networks 뷰에서 HMI 를 CPU 에 드래그' 와 동일한 동작을 수행합니다.\\n\\n### 단계\\n1. HMI/CPU NetworkInterface 의 Node 를 같은 Subnet 에 연결 (Node.ConnectToSubnet / CreateAndConnectToSubnet 사용)\\n2. (옵션) IP 주소 설정 (Node.SetAttribute('Address'))\\n3. (옵션, 기본 true) HMI Connection 을 partner=CPU DeviceItem 으로 생성\\n\\n### Subnet 처리 규칙\\n- CPU 가 이미 subnet 에 연결돼 있으면 그것을 재사용 → HMI 도 거기 연결\\n- 양쪽 다 미연결이면 ?subnet= 이름으로 새 Subnet 을 생성 (기본 PN/IE_1)\\n\\n### Integrated connection 한계\\nV20 Openness 의 HmiTarget.Connections 는 GUI 로 만든 integrated connection 을 enumerate 하지 못합니다. API 로 만든 connection 은 보일 수 있는지 확인용으로 사용하세요."",
        ""parameters"": [
          {""name"": ""hmi"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""HMI 디바이스 아이템 이름""},
          {""name"": ""cpu"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""파트너 CPU DeviceItem 이름""},
          {""name"": ""subnet"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""Subnet 이름 (CPU 가 미연결일 때만 사용 — 기본 PN/IE_1)""},
          {""name"": ""hmiIp"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""HMI 의 IP 주소 (옵션)""},
          {""name"": ""cpuIp"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""CPU 의 IP 주소 (옵션)""},
          {""name"": ""createConnection"", ""in"": ""query"", ""schema"": {""type"": ""boolean"", ""default"": true}, ""description"": ""HMI Connection 도 같이 생성할지""},
          {""name"": ""connectionName"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""생성할 Connection 이름 (예: HMI_Connection_2)""},
          {""name"": ""typeId"", ""in"": ""query"", ""schema"": {""type"": ""string"", ""default"": ""HmiS7Connection""}, ""description"": ""Connection TypeIdentifier""}
        ],
        ""responses"": {
          ""200"": {""description"": ""Subnet 셋업 + (옵션) Connection 생성 결과""},
          ""400"": {""description"": ""cpu 파라미터 누락""},
          ""404"": {""description"": ""HMI / CPU / NetworkInterface / Node 를 찾을 수 없음""},
          ""500"": {""description"": ""Subnet 연결 실패""}
        }
      }
    },
    ""/api/project/export-aml"": {
      ""get"": {
        ""tags"": [""AML""],
        ""summary"": ""프로젝트를 AML 파일로 내보내기 (CAx Export)"",
        ""description"": ""TIA Portal 프로젝트의 하드웨어 구성을 AutomationML(AML) 형식으로 내보냅니다.\\n\\nCAx(Computer-Aided Exchange) 표준을 사용하여 디바이스 구성, 네트워크 토폴로지, I/O 매핑 등을 AML 파일로 export합니다.\\n\\n내보낸 파일은 AML + 로그 파일이 포함된 ZIP으로 다운로드됩니다.\\n\\n**참고**: HMI Unified 패널은 프로젝트 레벨 export에서 누락될 수 있습니다. 개별 HMI 디바이스 export는 /api/device/export-aml을 사용하세요."",
        ""parameters"": [
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름 (생략 시 첫 번째 프로젝트)""}
        ],
        ""responses"": {
          ""200"": {""description"": ""AML + 로그 파일이 포함된 ZIP 다운로드""},
          ""404"": {""description"": ""프로젝트를 찾을 수 없음""},
          ""500"": {""description"": ""AML export 실패""}
        }
      }
    },
    ""/api/project/import-aml"": {
      ""post"": {
        ""tags"": [""AML""],
        ""summary"": ""AML 파일을 프로젝트에 가져오기 (CAx Import)"",
        ""description"": ""AutomationML(AML) 파일을 TIA Portal 프로젝트에 가져옵니다.\\n\\n두 가지 방식으로 사용할 수 있습니다:\\n1. **file 파라미터**: 서버 로컬 경로에 있는 AML 파일 지정\\n2. **POST body**: AML 파일을 직접 업로드\\n\\nimport 결과 로그가 응답에 포함됩니다."",
        ""parameters"": [
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름 (생략 시 첫 번째 프로젝트)""},
          {""name"": ""file"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""서버 로컬 AML 파일 경로 (POST body 업로드 시 생략 가능)""},
          {""name"": ""option"", ""in"": ""query"", ""schema"": {""type"": ""string"", ""enum"": [""MoveToParkingLot"", ""OverwriteTiaDevice"", ""RetainTiaDevice""], ""default"": ""MoveToParkingLot""}, ""description"": ""충돌 시 처리 방식 — MoveToParkingLot: 기존 디바이스를 파킹롯으로 이동, OverwriteTiaDevice: 기존 디바이스 덮어쓰기, RetainTiaDevice: 기존 디바이스 유지""}
        ],
        ""requestBody"": {
          ""description"": ""AML 파일 업로드 (file 파라미터 미사용 시)"",
          ""content"": {
            ""application/octet-stream"": {
              ""schema"": {""type"": ""string"", ""format"": ""binary""}
            }
          }
        },
        ""responses"": {
          ""200"": {""description"": ""AML import 성공 — 프로젝트명, 파일 경로, 로그 내용 반환""},
          ""400"": {""description"": ""파일 미지정 및 body 비어있음""},
          ""404"": {""description"": ""프로젝트 또는 파일을 찾을 수 없음""},
          ""500"": {""description"": ""AML import 실패""}
        }
      }
    },
    ""/api/device/export-aml"": {
      ""get"": {
        ""tags"": [""AML""],
        ""summary"": ""개별 디바이스를 AML 파일로 내보내기 (Device-level CAx Export)"",
        ""description"": ""특정 디바이스(HMI 포함)를 개별적으로 AML 형식으로 내보냅니다.\\n\\n프로젝트 레벨 export에서 누락되는 HMI Unified 패널도 이 API로 개별 export할 수 있습니다.\\n\\nCaxProvider.Export(Device, FileInfo) API를 사용합니다."",
        ""parameters"": [
          {""name"": ""device"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""디바이스 이름 (예: HMI_1, PLC_1)""},
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름 (생략 시 첫 번째 프로젝트)""}
        ],
        ""responses"": {
          ""200"": {""description"": ""AML + 로그 파일이 포함된 ZIP 다운로드""},
          ""400"": {""description"": ""device 파라미터 누락""},
          ""404"": {""description"": ""프로젝트 또는 디바이스를 찾을 수 없음""},
          ""500"": {""description"": ""디바이스 AML export 실패""}
        }
      }
    },
    ""/api/library/list"": {
      ""get"": {
        ""tags"": [""Library""],
        ""summary"": ""프로젝트 라이브러리 목록 조회"",
        ""description"": ""프로젝트 라이브러리의 Master Copy와 Library Type 전체 목록을 폴더 경로와 함께 반환합니다."",
        ""parameters"": [
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름 (생략 시 첫 번째 프로젝트)""}
        ],
        ""responses"": {
          ""200"": {""description"": ""Master Copy + Library Type 목록 (이름, 경로, 버전 정보)""},
          ""404"": {""description"": ""프로젝트를 찾을 수 없음""}
        }
      }
    },
    ""/api/library/export"": {
      ""get"": {
        ""tags"": [""Library""],
        ""summary"": ""라이브러리 항목 개별 Export (Documents 포맷 ZIP)"",
        ""description"": ""Library Type을 ExportAsDocuments 포맷으로 내보냅니다 (ZIP 파일). /api/library/import에서 이 ZIP을 그대로 업로드하면 됩니다.\\n\\nMasterCopy는 직접 Export 불가 — /api/library/copy-to-plc 사용."",
        ""parameters"": [
          {""name"": ""name"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""항목 이름""},
          {""name"": ""kind"", ""in"": ""query"", ""schema"": {""type"": ""string"", ""enum"": [""masterCopy"", ""libraryType""]}, ""description"": ""항목 종류 (기본: masterCopy)""},
          {""name"": ""path"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""폴더 경로 (예: Motors/S210). 생략 시 전체에서 검색""},
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름""}
        ],
        ""responses"": {
          ""200"": {""description"": ""XML 파일 다운로드""},
          ""404"": {""description"": ""항목을 찾을 수 없음""}
        }
      }
    },
    ""/api/library/export/zip"": {
      ""get"": {
        ""tags"": [""Library""],
        ""summary"": ""라이브러리 전체 ZIP Export (Documents 포맷)"",
        ""description"": ""프로젝트 라이브러리의 모든 LibraryType을 ExportAsDocuments 포맷으로 ZIP 내보냅니다.\\n\\nZIP 구조: LibraryTypes/[폴더]/[타입명]/[documents 파일들]"",
        ""parameters"": [
          {""name"": ""kind"", ""in"": ""query"", ""schema"": {""type"": ""string"", ""enum"": [""all"", ""masterCopy"", ""libraryType""]}, ""description"": ""내보낼 종류 (기본: all)""},
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름""}
        ],
        ""responses"": {
          ""200"": {""description"": ""ZIP 파일 다운로드 (X-Export-Success, X-Export-Failed 헤더 포함)""},
          ""404"": {""description"": ""프로젝트를 찾을 수 없음""}
        }
      }
    },
    ""/api/library/import"": {
      ""post"": {
        ""tags"": [""Library""],
        ""summary"": ""라이브러리에 XML Import"",
        ""description"": ""라이브러리에 Import합니다.\\n\\n**MasterCopy** (kind=masterCopy): XML을 PLC에 import 후 블록에서 MasterCopy를 생성합니다. ?plc= 파라미터 필수.\\n**LibraryType** (kind=libraryType): /api/library/export에서 받은 ZIP을 그대로 POST하면 CreateFromDocuments로 import됩니다. 단일 XML 파일도 지원."",
        ""parameters"": [
          {""name"": ""kind"", ""in"": ""query"", ""schema"": {""type"": ""string"", ""enum"": [""masterCopy"", ""libraryType""]}, ""description"": ""Import 대상 (기본: masterCopy)""},
          {""name"": ""plc"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""PLC DeviceItem 이름 (masterCopy import시 필수)""},
          {""name"": ""path"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""대상 폴더 경로 (예: Motors/S210). 생략 시 루트 폴더""},
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름""}
        ],
        ""requestBody"": {
          ""required"": true,
          ""content"": {
            ""application/xml"": {
              ""schema"": {""type"": ""string"", ""format"": ""binary""}
            }
          }
        },
        ""responses"": {
          ""200"": {""description"": ""Import 성공""},
          ""400"": {""description"": ""빈 요청 body 또는 파라미터 누락""},
          ""404"": {""description"": ""프로젝트 또는 PLC를 찾을 수 없음""},
          ""500"": {""description"": ""Import 실패""}
        }
      }
    },
    ""/api/library/copy-to-plc"": {
      ""get"": {
        ""tags"": [""Library""],
        ""summary"": ""라이브러리 항목을 PLC로 복사"",
        ""description"": ""MasterCopy 또는 LibraryType을 PLC 블록/타입 그룹에 복사합니다.\\n\\n- MasterCopy: PlcBlockComposition.CreateFrom(masterCopy)\\n- LibraryType: Export XML → PLC Import"",
        ""parameters"": [
          {""name"": ""name"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""라이브러리 항목 이름""},
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC DeviceItem 이름""},
          {""name"": ""path"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""라이브러리 폴더 경로""},
          {""name"": ""group"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""PLC 블록 그룹 경로 (MasterCopy만 해당)""},
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름""}
        ],
        ""responses"": {
          ""200"": {""description"": ""복사 성공""},
          ""404"": {""description"": ""항목 또는 PLC를 찾을 수 없음""}
        }
      }
    },
    ""/api/library/create-mastercopy"": {
      ""get"": {
        ""tags"": [""Library""],
        ""summary"": ""PLC 블록에서 MasterCopy 생성"",
        ""description"": ""PLC에 있는 블록(FB, FC, OB, DB 등)을 프로젝트 라이브러리의 MasterCopy로 생성합니다."",
        ""parameters"": [
          {""name"": ""plc"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""PLC DeviceItem 이름""},
          {""name"": ""block"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""블록 이름""},
          {""name"": ""path"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""MasterCopy 대상 폴더 경로. 생략 시 루트""},
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름""}
        ],
        ""responses"": {
          ""200"": {""description"": ""MasterCopy 생성 성공""},
          ""404"": {""description"": ""PLC 또는 블록을 찾을 수 없음""}
        }
      }
    },
    ""/api/global-library/import"": {
      ""post"": {
        ""tags"": [""Global Library""],
        ""summary"": ""[Step 1] .al20 파일 업로드 → Global Library 열기"",
        ""description"": "".al20 폴더를 ZIP으로 압축하여 업로드하면 Global Library로 열어서 메모리에 유지합니다.\\n열린 후 /api/global-library/to-project-library 로 Project Library에 복사할 수 있습니다.\\n\\n**사용법:**\\n1. .al20 폴더를 ZIP으로 압축 (예: Library1.al20 폴더 → Library1.al20.zip)\\n2. ZIP 파일 업로드\\n\\n**curl 예시:**\\n```\\ncurl -X POST 'http://localhost:8099/api/global-library/import?filename=Library1.al20.zip' --data-binary @Library1.al20.zip\\n```"",
        ""parameters"": [
          {""name"": ""filename"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""ZIP 파일명 (예: Library1.al20.zip). .zip과 .al20 확장자를 제거한 값이 라이브러리 식별자로 사용됩니다.""},
          {""name"": ""name"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""(하위 호환) 라이브러리 이름 직접 지정. filename과 동시 지정 시 name 우선""}
        ],
        ""requestBody"": {
          ""required"": true,
          ""content"": {
            ""application/octet-stream"": {
              ""schema"": {""type"": ""string"", ""format"": ""binary""}
            }
          }
        },
        ""responses"": {
          ""200"": {""description"": ""Global Library 열기 성공 + 내용물 목록 (MasterCopy, LibraryType)""},
          ""500"": {""description"": ""열기 실패""}
        }
      }
    },
    ""/api/global-library/to-project-library"": {
      ""get"": {
        ""tags"": [""Global Library""],
        ""summary"": ""[Step 2] 열린 Global Library → Project Library 전체 복사 (Export/Import 우회)"",
        ""description"": ""import로 열어둔 Global Library의 모든 MasterCopy와 LibraryType을 프로젝트 라이브러리로 복사합니다.\\nGlobal Library가 1개만 열려있으면 ?lib= 생략 가능.\\n\\n**참고**: 이 엔드포인트는 ExportAsDocuments→CreateFromDocuments 우회로를 씁니다. 더 빠르고 정확한 네이티브 API 가 필요하면 /api/global-library/update-project-library 를 사용하세요."",
        ""parameters"": [
          {""name"": ""lib"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""Global Library 이름 (import 시 ?name= 으로 지정한 값). 1개만 열려있으면 생략 가능""},
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름. 생략 시 첫 번째 프로젝트""},
          {""name"": ""targetPath"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""Project Library 내 대상 폴더 경로. 생략 시 루트""}
        ],
        ""responses"": {
          ""200"": {""description"": ""복사 결과 (성공/실패 항목 목록)""},
          ""404"": {""description"": ""Global Library 또는 프로젝트를 찾을 수 없음""}
        }
      }
    },
    ""/api/global-library/update-project-library"": {
      ""get"": {
        ""tags"": [""Global Library""],
        ""summary"": ""Global Library → Project Library Types 네이티브 복사 (UpdateLibrary)"",
        ""description"": ""TIA Openness 네이티브 API LibraryType.UpdateLibrary / ILibrary.UpdateLibrary 를 사용하여 Global Library 의 LibraryType들을 Project Library 로 직접 복사합니다.\\n\\n기존 /api/global-library/to-project-library 가 ExportAsDocuments→CreateFromDocuments 우회로를 쓰는 것과 달리 in-process 직접 복사이므로 더 빠르고 Faceplate 등 Documents 미지원 타입도 처리 가능합니다.\\n\\n### 동작 모드\\n- type 미지정: 모든 LibraryType + 사용자 폴더 일괄 복사 (ILibrary.UpdateLibrary 호출)\\n- type 지정: 단일 LibraryType만 복사 (LibraryType.UpdateLibrary 호출)\\n\\n### Update 의미\\n타깃에 동일 GUID 타입이 이미 있으면 새 version 으로 추가, 없으면 새로 생성. 중복 생성 아님.\\n\\n### 옵션 (셋 다 모두 지정해야 4-arg overload 사용, 아니면 기본 overload)\\n- forceUpdate: 기본 SetOnlyHigherUpdatedVersionAsDefault\\n- deleteUnused: 기본 DoNotDelete\\n- structure: ProjectLibrary 는 RetainStructure 만 지원 (다른 값 시 500 에러)"",
        ""parameters"": [
          {""name"": ""lib"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""Global Library 이름. 1개만 열려있으면 생략 가능""},
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""대상 프로젝트 이름. 생략 시 첫 번째 프로젝트""},
          {""name"": ""type"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""단일 LibraryType 이름 (생략 시 전체 일괄 복사)""},
          {""name"": ""path"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""type 지정 시 Global Library 내 폴더 경로 (예: SubFolder1/SubA)""},
          {""name"": ""forceUpdate"", ""in"": ""query"", ""schema"": {""type"": ""string"", ""enum"": [""SetOnlyHigherUpdatedVersionAsDefault"", ""ForceSetAnyUpdatedVersionAsDefault"", ""NoDefaultVersionChange""]}, ""description"": ""ForceUpdateMode""},
          {""name"": ""deleteUnused"", ""in"": ""query"", ""schema"": {""type"": ""string"", ""enum"": [""AutomaticallyDelete"", ""DoNotDelete""]}, ""description"": ""DeleteUnusedVersionsMode""},
          {""name"": ""structure"", ""in"": ""query"", ""schema"": {""type"": ""string"", ""enum"": [""UpdateStructure"", ""RetainStructure"", ""CancelIfStructureConflicts""]}, ""description"": ""StructureConflictResolutionMode (ProjectLibrary 는 RetainStructure 만 지원)""}
        ],
        ""responses"": {
          ""200"": {""description"": ""복사 완료 — 단일 모드 시 type 정보, 일괄 모드 시 copiedTypeCount/copiedFolderCount""},
          ""400"": {""description"": ""enum 값 오류 또는 여러 라이브러리가 열려있는데 lib 미지정""},
          ""404"": {""description"": ""Global Library / 프로젝트 / LibraryType 을 찾을 수 없음""},
          ""500"": {""description"": ""UpdateLibrary 실행 실패 (StructureConflict 등)""}
        }
      }
    },
    ""/api/global-library/open"": {
      ""post"": {
        ""tags"": [""Global Library""],
        ""summary"": ""Global Library (.al20) 파일 열기"",
        ""description"": ""로컬 경로의 .al20 파일을 TIA Portal에 로드합니다. 열린 후 /api/global-library/list와 /api/global-library/copy-to-project로 내용을 조회하고 복사할 수 있습니다."",
        ""requestBody"": {
          ""required"": true,
          ""content"": {
            ""application/json"": {
              ""schema"": {
                ""type"": ""object"",
                ""properties"": {
                  ""path"": {""type"": ""string"", ""description"": "".al20 파일의 절대 경로""},
                  ""mode"": {""type"": ""string"", ""enum"": [""readonly"", ""readwrite""], ""description"": ""열기 모드 (기본: readonly)""}
                },
                ""required"": [""path""]
              }
            }
          }
        },
        ""responses"": {
          ""200"": {""description"": ""Global Library 열기 성공""},
          ""404"": {""description"": ""파일을 찾을 수 없음""}
        }
      }
    },
    ""/api/global-library/list"": {
      ""get"": {
        ""tags"": [""Global Library""],
        ""summary"": ""열린 Global Library 내용 조회"",
        ""description"": ""?name= 생략 시 현재 열려있는 모든 Global Library 목록 반환. ?name= 지정 시 해당 라이브러리의 MasterCopy와 LibraryType 전체 목록을 반환합니다."",
        ""parameters"": [
          {""name"": ""name"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""Global Library 이름 (파일명에서 확장자 제외). 생략 시 열린 라이브러리 목록""}
        ],
        ""responses"": {
          ""200"": {""description"": ""라이브러리 내용 목록""},
          ""404"": {""description"": ""열려있는 라이브러리가 없거나 이름이 일치하지 않음""}
        }
      }
    },
    ""/api/global-library/copy-to-project"": {
      ""get"": {
        ""tags"": [""Global Library""],
        ""summary"": ""Global Library 항목을 프로젝트로 복사"",
        ""description"": ""Global Library의 MasterCopy 또는 LibraryType을 프로젝트 라이브러리 또는 PLC로 복사합니다.\\n\\n**target=project-library** (기본): Project Library로 복사\\n**target=plc**: PLC 블록/타입으로 직접 복사 (?plc= 필수)"",
        ""parameters"": [
          {""name"": ""lib"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""Global Library 이름""},
          {""name"": ""name"", ""in"": ""query"", ""required"": true, ""schema"": {""type"": ""string""}, ""description"": ""복사할 항목 이름 (MasterCopy 또는 LibraryType)""},
          {""name"": ""path"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""Global Library 내 항목 폴더 경로""},
          {""name"": ""target"", ""in"": ""query"", ""schema"": {""type"": ""string"", ""enum"": [""project-library"", ""plc""]}, ""description"": ""복사 대상 (기본: project-library)""},
          {""name"": ""plc"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""target=plc 일 때 대상 PLC 이름""},
          {""name"": ""targetPath"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""대상 폴더 경로. 생략 시 루트""},
          {""name"": ""project"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""프로젝트 이름""}
        ],
        ""responses"": {
          ""200"": {""description"": ""복사 성공""},
          ""404"": {""description"": ""라이브러리 또는 항목을 찾을 수 없음""}
        }
      }
    },
    ""/api/global-library/close"": {
      ""get"": {
        ""tags"": [""Global Library""],
        ""summary"": ""Global Library 닫기"",
        ""description"": ""?name= 지정 시 해당 라이브러리만 닫고, 생략 시 열려있는 모든 Global Library를 닫습니다."",
        ""parameters"": [
          {""name"": ""name"", ""in"": ""query"", ""schema"": {""type"": ""string""}, ""description"": ""닫을 Global Library 이름. 생략 시 전체 닫기""}
        ],
        ""responses"": {
          ""200"": {""description"": ""닫기 성공""}
        }
      }
    }
  }
}";
            resp.StatusCode = 200;
            resp.ContentType = "application/json; charset=utf-8";
            byte[] buf = Encoding.UTF8.GetBytes(spec);
            resp.ContentLength64 = buf.Length;
            resp.OutputStream.Write(buf, 0, buf.Length);
            resp.Close();
        }

        static void ServeDevicePropertiesUI(HttpListenerResponse resp)
        {
            string html = @"<!DOCTYPE html>
<html lang=""ko"">
<head>
<meta charset=""UTF-8"">
<title>CPU 속성 편집 · TIA Openness</title>
<style>
* { box-sizing: border-box; }
body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; margin: 0; height: 100vh; display: flex; flex-direction: column; font-size: 13px; color: #222; background: white; }
.toolbar { background: #1b1b1b; color: white; padding: 10px 16px; display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
.toolbar strong { font-size: 14px; }
.toolbar a { color: #79c0ff; text-decoration: none; font-size: 12px; }
.toolbar a:hover { text-decoration: underline; }
.toolbar label { font-size: 12px; color: #bbb; }
.toolbar select, .toolbar button { padding: 5px 10px; font-size: 13px; border-radius: 3px; border: 1px solid #444; background: #2a2a2a; color: #eee; }
.toolbar button { background: #0d6efd; color: white; border: none; cursor: pointer; }
.toolbar button:hover { background: #0b5ed7; }
.toolbar .status { margin-left: auto; font-size: 12px; color: #aaa; }
.main { flex: 1; display: flex; overflow: hidden; min-height: 0; }
.sidebar { width: 340px; background: #f4f4f4; border-right: 1px solid #c5c5c5; overflow: auto; flex-shrink: 0; }
.content { flex: 1; overflow: auto; display: flex; flex-direction: column; }
.content-body { padding: 14px 20px; flex: 1; overflow: auto; }
.tree-node { padding: 4px 8px; cursor: pointer; white-space: nowrap; user-select: none; border-left: 3px solid transparent; }
.tree-node:hover { background: #e7e7e7; }
.tree-node.selected { background: #cfe2ff; color: #052c65; font-weight: 500; border-left-color: #0d6efd; }
.tree-node .badge { display: inline-block; font-size: 10px; color: #777; margin-left: 6px; font-weight: normal; }
.breadcrumb { font-size: 12px; color: #666; margin-bottom: 6px; }
.panel-header { font-weight: 600; font-size: 15px; padding: 4px 0 12px 0; border-bottom: 1px solid #ddd; margin-bottom: 12px; display: flex; justify-content: space-between; align-items: baseline; }
.panel-header .count { font-weight: normal; color: #888; font-size: 12px; }
table.attr-table { width: 100%; border-collapse: collapse; font-size: 13px; }
table.attr-table th, table.attr-table td { padding: 5px 8px; border-bottom: 1px solid #eee; text-align: left; vertical-align: middle; }
table.attr-table th { background: #f8f9fa; font-weight: 600; position: sticky; top: 0; border-bottom: 2px solid #dee2e6; }
table.attr-table td.name { font-weight: 500; color: #333; width: 28%; word-break: break-all; }
table.attr-table td.val { width: 36%; color: #555; font-family: 'Consolas', monospace; font-size: 12px; word-break: break-all; }
table.attr-table td.edit { width: 30%; }
table.attr-table td.mode { width: 6%; text-align: center; white-space: nowrap; }
table.attr-table input { width: 100%; padding: 3px 6px; font-size: 12px; font-family: 'Consolas', monospace; border: 1px solid #ccc; border-radius: 2px; }
table.attr-table input:focus { outline: none; border-color: #0d6efd; box-shadow: 0 0 0 2px #0d6efd33; }
table.attr-table input[readonly] { background: #f1f3f5; color: #888; cursor: not-allowed; }
table.attr-table tr.changed td.edit input { background: #fff3cd; border-color: #ffc107; }
table.attr-table tr.changed td.name::before { content: '● '; color: #fd7e14; }
table.attr-table tr.readonly-row td.name { color: #666; }
table.attr-table tr.danger-row td.name::after { content: ' ⚠'; color: #b45309; cursor: help; }
.badge-ro { display: inline-block; padding: 1px 6px; border-radius: 3px; font-size: 10px; font-weight: 600; background: #e9ecef; color: #495057; }
.badge-rw { display: inline-block; padding: 1px 6px; border-radius: 3px; font-size: 10px; font-weight: 600; background: #d1e7dd; color: #0f5132; }
.badge-rw.danger { background: #fff3cd; color: #b45309; }
.legend { display: flex; gap: 12px; font-size: 11px; color: #666; margin: 4px 0 10px 0; flex-wrap: wrap; }
.legend span { display: inline-flex; align-items: center; gap: 4px; }
.exec-bar { background: #fafafa; border-top: 1px solid #ddd; padding: 10px 20px; display: flex; gap: 12px; align-items: center; flex-shrink: 0; }
.exec-bar button { background: #198754; color: white; border: none; padding: 8px 20px; border-radius: 3px; cursor: pointer; font-size: 14px; font-weight: 600; }
.exec-bar button:hover:not(:disabled) { background: #157347; }
.exec-bar button:disabled { background: #6c757d; cursor: not-allowed; }
.exec-bar .counter { color: #666; font-size: 12px; }
.log { margin-top: 14px; background: #f8f9fa; border: 1px solid #dee2e6; border-radius: 3px; padding: 10px 12px; font-family: 'Consolas', monospace; font-size: 12px; max-height: 240px; overflow: auto; }
.log .ok { color: #0f5132; }
.log .err { color: #842029; font-weight: 600; }
.empty { padding: 20px; color: #888; text-align: center; font-size: 13px; }
.hint { color: #888; font-size: 12px; margin-top: 6px; }
.spinner { display: inline-block; width: 12px; height: 12px; border: 2px solid #ccc; border-top-color: #0d6efd; border-radius: 50%; animation: spin 0.6s linear infinite; vertical-align: -2px; margin-right: 6px; }
@keyframes spin { to { transform: rotate(360deg); } }
.ip-panel { background: #eef6ff; border-bottom: 1px solid #b6daff; padding: 8px 16px; display: flex; align-items: center; gap: 8px; flex-wrap: wrap; font-size: 12px; }
.ip-panel strong { color: #084c84; margin-right: 6px; }
.ip-panel label { color: #555; }
.ip-panel input { padding: 4px 8px; font-size: 12px; font-family: 'Consolas', monospace; border: 1px solid #b6daff; border-radius: 3px; background: white; }
.ip-panel input[type=text] { width: 140px; }
.ip-panel input[type=number] { width: 60px; }
.ip-panel button { background: #0d6efd; color: white; border: none; padding: 5px 14px; border-radius: 3px; cursor: pointer; font-size: 12px; font-weight: 600; }
.ip-panel button:hover { background: #0b5ed7; }
.ip-panel button:disabled { background: #6c757d; cursor: not-allowed; }
.ip-panel .ip-result { margin-left: 6px; font-family: 'Consolas', monospace; font-size: 12px; }
</style>
</head>
<body>
<div class=""toolbar"">
  <strong>⚙️ CPU 속성 편집</strong>
  <a href=""/"">← Swagger UI</a>
  <a href=""/ui/network-properties"">🌐 네트워크 속성 편집</a>
  <span style=""margin-left:20px;width:1px;height:20px;background:#444""></span>
  <label>프로젝트</label>
  <select id=""sel-project""></select>
  <label>디바이스</label>
  <select id=""sel-device""></select>
  <button onclick=""loadTree()"">🔄 트리 새로고침</button>
  <span class=""status"" id=""conn-status""></span>
</div>
<div class=""ip-panel"" title=""선택된 디바이스의 NetworkInterface Node 에 IP/SubnetMask 를 직접 적용 (Subnet 미연결 상태에서도 가능)"">
  <strong>🌐 Interface IP</strong>
  <label>Address</label>
  <input type=""text"" id=""ip-address"" placeholder=""192.168.0.10"">
  <label>SubnetMask</label>
  <input type=""text"" id=""ip-mask"" placeholder=""255.255.255.0 (선택)"">
  <label>If#</label>
  <input type=""number"" id=""ip-if-idx"" placeholder=""0"" min=""0"">
  <button id=""ip-apply-btn"" onclick=""applyInterfaceIp()"">Apply</button>
  <span class=""ip-result"" id=""ip-result""></span>
</div>
<div class=""main"">
  <div class=""sidebar"" id=""sidebar"">
    <div class=""empty"">프로젝트/디바이스를 선택하세요</div>
  </div>
  <div class=""content"">
    <div class=""content-body"" id=""content"">
      <div class=""panel-header""><span>DeviceItem 속성</span></div>
      <div class=""hint"">좌측 트리에서 CPU / 인터페이스 / 포트 등 항목을 선택하세요.</div>
      <div class=""hint"">TIA Portal의 Properties 패널처럼 모든 속성을 확인하고 편집한 뒤 Execute 버튼으로 PUT /api/device/item-attributes 호출로 일괄 적용됩니다.</div>
    </div>
  </div>
</div>

<script>
var state = { projects: [], currentProject: null, devices: [], currentDevice: null, tree: [], selected: null, attrs: [], edited: {}, extraSections: [], extraEdits: {} };

function esc(s) { return String(s == null ? '' : s).replace(/[&<>""']/g, function(c){ return {'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;',""'"":'&#39;'}[c]; }); }
function setStatus(t) { document.getElementById('conn-status').textContent = t; }

async function api(url, opts) {
  var r = await fetch(url, opts);
  var data;
  try { data = await r.json(); } catch(e) { throw new Error('JSON 파싱 실패 · HTTP ' + r.status); }
  if (!r.ok || data.error) throw new Error(data.error || ('HTTP ' + r.status));
  return data;
}

async function loadProjects() {
  setStatus('프로젝트 로딩...');
  try {
    var data = await api('/api/projects');
    state.projects = data.projects || [];
    var sel = document.getElementById('sel-project');
    if (!state.projects.length) {
      sel.innerHTML = '<option value="""">(없음)</option>';
      setStatus('프로젝트 없음 · TIA Portal 연결 필요');
      return;
    }
    sel.innerHTML = state.projects.map(function(p){ return '<option value=""' + esc(p.name) + '"">' + esc(p.name) + '</option>'; }).join('');
    state.currentProject = state.projects[0].name;
    setStatus('프로젝트 ' + state.projects.length + '개');
    await loadDevices();
  } catch(e) {
    setStatus('오류: ' + e.message + ' · /api/connect 먼저 호출하세요');
  }
}

async function loadDevices() {
  if (!state.currentProject) return;
  var sel = document.getElementById('sel-device');
  sel.innerHTML = '<option>로딩...</option>';
  try {
    var data = await api('/api/devices?project=' + encodeURIComponent(state.currentProject));
    state.devices = data.devices || [];
    if (!state.devices.length) {
      sel.innerHTML = '<option value="""">(없음)</option>';
      document.getElementById('sidebar').innerHTML = '<div class=""empty"">디바이스가 없습니다</div>';
      return;
    }
    sel.innerHTML = state.devices.map(function(d){ return '<option value=""' + esc(d.name) + '"">' + esc(d.name) + ' · ' + esc(d.type || '') + '</option>'; }).join('');
    state.currentDevice = state.devices[0].name;
    await loadTree();
  } catch(e) {
    sel.innerHTML = '<option>오류</option>';
    document.getElementById('sidebar').innerHTML = '<div class=""empty"">디바이스 로딩 실패: ' + esc(e.message) + '</div>';
  }
}

async function loadTree() {
  var sb = document.getElementById('sidebar');
  if (!state.currentDevice) { sb.innerHTML = '<div class=""empty"">디바이스가 없습니다</div>'; return; }
  sb.innerHTML = '<div class=""empty""><span class=""spinner""></span>트리 로딩...</div>';
  try {
    var data = await api('/api/device/items?device=' + encodeURIComponent(state.currentDevice) + '&project=' + encodeURIComponent(state.currentProject || ''));
    state.tree = data.items || [];
    renderTree();
  } catch(e) {
    sb.innerHTML = '<div class=""empty"">트리 로딩 실패: ' + esc(e.message) + '</div>';
  }
}

function renderTree() {
  var parts = [];
  function walk(nodes, parentPath) {
    nodes.forEach(function(n) {
      var path = parentPath ? (parentPath + '/' + n.name) : n.name;
      var indent = (n.depth || 0) * 14 + 8;
      parts.push('<div class=""tree-node"" data-path=""' + esc(path) + '"" style=""padding-left:' + indent + 'px"" title=""' + esc(n.type || '') + '"">'
        + esc(n.name) + '<span class=""badge"">' + esc(n.classification || '') + '</span></div>');
      if (n.children && n.children.length) walk(n.children, path);
    });
  }
  walk(state.tree, '');
  document.getElementById('sidebar').innerHTML = parts.join('') || '<div class=""empty"">하위 아이템 없음</div>';
  document.querySelectorAll('.tree-node').forEach(function(el) {
    el.onclick = function() { selectItem(el.getAttribute('data-path'), el); };
  });
}

async function selectItem(itemPath, el) {
  document.querySelectorAll('.tree-node.selected').forEach(function(e){ e.classList.remove('selected'); });
  if (el) el.classList.add('selected');
  state.selected = itemPath;
  state.edited = {};
  var content = document.getElementById('content');
  content.innerHTML = '<div class=""breadcrumb"">' + esc(state.currentProject || '') + ' / ' + esc(state.currentDevice || '') + ' / <strong>' + esc(itemPath) + '</strong></div><div class=""panel-header""><span>속성</span></div><div><span class=""spinner""></span>속성 로딩...</div>';
  try {
    var data = await api('/api/device/item-attributes?device=' + encodeURIComponent(state.currentDevice) + '&item=' + encodeURIComponent(itemPath) + '&project=' + encodeURIComponent(state.currentProject || ''));
    state.attrs = data.attributes || [];
    state.extraSections = data.extraSections || [];
    state.extraEdits = {};
    renderAttrs();
  } catch(e) {
    content.innerHTML = '<div class=""breadcrumb"">' + esc(itemPath) + '</div><div class=""log err"">속성 로딩 실패: ' + esc(e.message) + '</div>';
  }
}

// 하드웨어 주소/식별자 등 수정 시 영향도 높은 속성 패턴 (ReadWrite이지만 위험)
var DANGER_PATTERNS = [
  /^IpAddress$/i, /^SubnetMask$/i, /^RouterAddress$/i,
  /MacAddress/i, /DeviceNumber/i, /HwIdentifier/i, /Identifier$/i,
  /^ArticleNumber$/i, /^FirmwareVersion$/i,
  /^Classification$/i, /^PositionNumber$/i,
  /^Name$/i, /ProfinetDeviceName/i
];
function isDanger(attrName) {
  for (var i = 0; i < DANGER_PATTERNS.length; i++) if (DANGER_PATTERNS[i].test(attrName)) return true;
  return false;
}

function renderAttrs() {
  var roCount = 0, rwCount = 0, dangerCount = 0;
  var rows = state.attrs.map(function(a, i) {
    var val = a.value == null ? '' : a.value;
    var isRO = (a.accessMode === 'Read');
    var danger = !isRO && isDanger(a.name);
    if (isRO) roCount++; else rwCount++;
    if (danger) dangerCount++;

    var badge;
    if (isRO) badge = '<span class=""badge-ro"" title=""읽기 전용"">R</span>';
    else if (danger) badge = '<span class=""badge-rw danger"" title=""수정 가능하지만 하드웨어/식별자 속성 - 주의"">RW⚠</span>';
    else badge = '<span class=""badge-rw"" title=""읽기/쓰기"">RW</span>';

    var inputAttrs = 'value=""' + esc(val) + '"" oninput=""markChanged(' + i + ', this.value)""';
    if (isRO) inputAttrs = 'value=""' + esc(val) + '"" readonly tabindex=""-1""';

    var typeTag = a.type ? ' <span style=""color:#888;font-size:11px"">[' + esc(a.type) + ']</span>' : '';
    var rowClass = 'data-idx=""' + i + '""';
    if (isRO) rowClass += ' class=""readonly-row""';
    else if (danger) rowClass += ' class=""danger-row""';

    return '<tr ' + rowClass + ' title=""' + esc(a.name) + (a.type ? ' : ' + esc(a.type) : '') + (isRO ? ' (읽기 전용)' : danger ? ' (하드웨어/식별자 - 수정 주의)' : '') + '"">'
      + '<td class=""name"">' + esc(a.name) + typeTag + '</td>'
      + '<td class=""mode"">' + badge + '</td>'
      + '<td class=""val"">' + esc(val === '' ? '<null>' : val) + '</td>'
      + '<td class=""edit""><input type=""text"" ' + inputAttrs + '></td>'
      + '</tr>';
  }).join('');

  var legend = '<div class=""legend"">'
    + '<span><span class=""badge-ro"">R</span> 읽기 전용 (비활성화)</span>'
    + '<span><span class=""badge-rw"">RW</span> 자유 편집</span>'
    + '<span><span class=""badge-rw danger"">RW⚠</span> 수정 가능하지만 하드웨어/식별자 관련 - 주의</span>'
    + '</div>';

  document.getElementById('content').innerHTML =
    '<div class=""breadcrumb"">' + esc(state.currentProject || '') + ' / ' + esc(state.currentDevice || '') + ' / <strong>' + esc(state.selected) + '</strong></div>'
    + '<div class=""panel-header""><span>속성 (DeviceItem)</span><span class=""count"">총 ' + state.attrs.length + '개 · 읽기전용 ' + roCount + ' · RW ' + rwCount + (dangerCount ? ' (그 중 주의 ' + dangerCount + ')' : '') + '</span></div>'
    + legend
    + '<table class=""attr-table""><thead><tr><th>이름 / 타입</th><th class=""mode"">모드</th><th>현재 값</th><th>새 값</th></tr></thead>'
    + '<tbody id=""attr-body"">' + rows + '</tbody></table>'
    + renderExtraSections()
    + '<div id=""log""></div>'
    + '<div class=""exec-bar""><button id=""exec-btn"" onclick=""execute()"" disabled>Execute (수정 0건)</button>'
    + '<span class=""counter"">편집된 속성만 PUT /api/device/item-attributes로 전송 · 읽기전용은 편집/전송 대상에서 자동 제외</span></div>';
}

// PROFINET 인터페이스의 IoConnector / IoController 같은 서브 객체 attribute 를 별도 섹션으로 렌더
function renderExtraSections() {
  if (!state.extraSections || state.extraSections.length === 0) return '';
  var html = '';

  state.extraSections.forEach(function(section, si) {
    var subAttrs = section.attributes || [];
    if (!subAttrs.length) return;
    var roCnt = 0, rwCnt = 0;
    var rows = subAttrs.map(function(a, ai) {
      var isRO = (a.accessMode === 'Read');
      if (isRO) roCnt++; else rwCnt++;
      var val = a.value == null ? '' : a.value;
      var badge = isRO ? '<span class=""badge-ro"">R</span>' : '<span class=""badge-rw"">RW</span>';
      var typeTag = a.type ? ' <span style=""color:#888;font-size:11px"">[' + esc(a.type) + (a.isEnum ? ' · enum' : '') + ']</span>' : '';
      var inputAttrs;
      if (isRO) {
        inputAttrs = '<input type=""text"" value=""' + esc(val) + '"" readonly tabindex=""-1"">';
      } else if (a.isEnum && a.enumValues && a.enumValues.length) {
        var opts = a.enumValues.map(function(ev){
          var sel = (ev === val) ? ' selected' : '';
          return '<option value=""' + esc(ev) + '""' + sel + '>' + esc(ev) + '</option>';
        }).join('');
        inputAttrs = '<select onchange=""markChangedExtra(' + si + ',' + ai + ', this.value)"" style=""width:100%"">' + opts + '</select>';
      } else {
        inputAttrs = '<input type=""text"" value=""' + esc(val) + '"" oninput=""markChangedExtra(' + si + ',' + ai + ', this.value)"">';
      }
      var displayVal = val;
      return '<tr data-section-idx=""' + si + '"" data-extra-attr-idx=""' + ai + '"">'
        + '<td class=""name"">' + esc(a.name) + typeTag + '</td>'
        + '<td class=""mode"">' + badge + '</td>'
        + '<td class=""val"">' + esc(displayVal === '' ? '<null>' : displayVal) + '</td>'
        + '<td class=""edit"">' + inputAttrs + '</td>'
        + '</tr>';
    }).join('');
    html += '<div style=""margin-top:22px;border-top:2px solid #6f42c1;padding-top:14px"">';
    html += '<div class=""panel-header""><span>' + esc(section.title || (section.kind + '[' + section.index + ']')) + '</span><span class=""count"">' + subAttrs.length + ' attributes · 읽기전용 ' + roCnt + ' · RW ' + rwCnt + ' · target=' + esc(section.target) + '</span></div>';
    html += '<table class=""attr-table""><thead><tr><th>이름 / 타입</th><th class=""mode"">모드</th><th>현재 값</th><th>새 값</th></tr></thead><tbody>' + rows + '</tbody></table>';
    html += '</div>';
  });
  return html;
}

async function applyMrpRoleToAll() {
  var sel = document.getElementById('mrp-bulk-role');
  var msg = document.getElementById('mrp-bulk-msg');
  if (!sel || !msg) return;
  var role = sel.value;
  msg.className = 'row-msg';
  msg.innerHTML = '<span class=""spinner""></span>PUT /api/device/mrp/set-all-roles · role=' + esc(role) + ' ...';

  try {
    var r = await fetch('/api/device/mrp/set-all-roles', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        project: state.currentProject,
        device: state.currentDevice,
        item: state.selected,
        role: role
      })
    });
    var d = await r.json();
    if (d.error) {
      msg.className = 'row-msg err';
      msg.innerHTML = '✗ ' + esc(d.error);
      return;
    }
    var lines = ['<strong>결과:</strong> 성공 <span style=""color:#0f5132"">' + (d.okCount || 0) + '</span> · 실패 <span style=""color:#842029"">' + (d.errCount || 0) + '</span> (총 ' + (d.count || 0) + ')'];
    (d.results || []).forEach(function(res){
      if (res.status === 'ok') {
        lines.push('  <span style=""color:#0f5132"">✓</span> #' + res.index + ' ' + esc(res.instanceName || '') + ' : ' + esc(res.oldValue || '') + ' → ' + esc(res.newValue || ''));
      } else {
        lines.push('  <span style=""color:#842029"">✗</span> #' + res.index + ' ' + esc(res.instanceName || '') + ' : ' + esc((res.error || '실패').split('\n')[0]));
      }
    });
    msg.innerHTML = lines.join('<br>');
    // 전체 다시 로드해서 화면 갱신
    setTimeout(function(){ selectItem(state.selected, document.querySelector('.tree-node.selected')); }, 600);
  } catch(e) {
    msg.className = 'row-msg err';
    msg.innerHTML = '✗ ' + esc(e.message);
  }
}

function markChangedExtra(sectionIdx, attrIdx, newVal) {
  var section = state.extraSections[sectionIdx];
  if (!section) return;
  var attr = (section.attributes || [])[attrIdx];
  if (!attr || attr.accessMode === 'Read') return;
  state.extraEdits = state.extraEdits || {};
  var key = (section.target || '') + ':' + attr.name;
  var oldVal = attr.value == null ? '' : String(attr.value);
  var row = document.querySelector('tr[data-section-idx=""' + sectionIdx + '""][data-extra-attr-idx=""' + attrIdx + '""]');
  if (String(newVal) !== oldVal) {
    state.extraEdits[key] = { name: attr.name, value: newVal, target: section.target };
    if (row) row.classList.add('changed');
  } else {
    delete state.extraEdits[key];
    if (row) row.classList.remove('changed');
  }
  updateExecBtnFromAll();
}

function updateExecBtnFromAll() {
  var main = Object.keys(state.edited || {}).length;
  var extra = Object.keys(state.extraEdits || {}).length;
  var btn = document.getElementById('exec-btn');
  if (btn) { btn.disabled = (main + extra) === 0; btn.textContent = 'Execute (수정 ' + (main + extra) + '건)'; }
}

function markChanged(idx, newVal) {
  var attr = state.attrs[idx];
  if (attr.accessMode === 'Read') return; // 읽기전용은 편집 상태에 등록 X
  var oldVal = attr.value == null ? '' : String(attr.value);
  var row = document.querySelector('tr[data-idx=""' + idx + '""]');
  if (String(newVal) !== oldVal) {
    state.edited[attr.name] = newVal;
    if (row) row.classList.add('changed');
  } else {
    delete state.edited[attr.name];
    if (row) row.classList.remove('changed');
  }
  updateExecBtnFromAll();
  return;
  // (구 코드 — updateExecBtnFromAll 로 통합됨)
  var n = Object.keys(state.edited).length;
  var btn = document.getElementById('exec-btn');
  if (btn) { btn.disabled = n === 0; btn.textContent = 'Execute (수정 ' + n + '건)'; }
}

async function execute() {
  var changes = Object.keys(state.edited).map(function(k){ return { name: k, value: state.edited[k] }; });
  Object.keys(state.extraEdits || {}).forEach(function(k){
    var e = state.extraEdits[k];
    changes.push({ name: e.name, value: e.value, target: e.target });
  });
  if (!changes.length) return;

  // 위험 속성이 포함된 경우 확인
  var dangers = changes.filter(function(c){ return isDanger(c.name); }).map(function(c){ return c.name; });
  if (dangers.length) {
    var ok = window.confirm('하드웨어/식별자 관련 속성이 포함되어 있습니다:\n\n · ' + dangers.join('\n · ') + '\n\n이 속성들은 컴파일/통신/디바이스 식별에 영향을 줄 수 있습니다.\n계속하시겠습니까?');
    if (!ok) return;
  }
  var btn = document.getElementById('exec-btn');
  btn.disabled = true; btn.textContent = '전송 중...';
  var log = document.getElementById('log');

  // MediaRedundancyRole 변경은 메인 DI attribute 가 아니라 모든 MRP 인스턴스에 일괄 적용 (set-all-roles)
  // — 단일/복수 무관, MrpInstancesOwner 가 있으면 자동 적용
  var hasMrpSections = (state.extraSections || []).some(function(s){ return s.kind === 'Feature:MrpInstancesOwner.MrpInstances'; });
  var mrpRoleChange = null;
  if (hasMrpSections) {
    for (var i = 0; i < changes.length; i++) {
      if (!changes[i].target && changes[i].name === 'MediaRedundancyRole') {
        mrpRoleChange = changes[i];
        changes.splice(i, 1);
        break;
      }
    }
  }

  var mrpResultLog = '';
  if (mrpRoleChange) {
    try {
      var rMrp = await fetch('/api/device/mrp/set-all-roles', {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ project: state.currentProject, device: state.currentDevice, item: state.selected, role: mrpRoleChange.value })
      });
      var dMrp = await rMrp.json();
      if (dMrp.error) {
        mrpResultLog = '<div class=""log err"">MRP 전체 적용 실패: ' + esc(dMrp.error) + '</div>';
      } else {
        var mLines = ['🔁 <strong>MediaRedundancyRole 전체 적용 → ' + esc(mrpRoleChange.value) + '</strong>: 성공 <span style=""color:#0f5132"">' + (dMrp.okCount || 0) + '</span> · 실패 <span style=""color:#842029"">' + (dMrp.errCount || 0) + '</span> (총 ' + (dMrp.count || 0) + '개)'];
        (dMrp.results || []).forEach(function(res){
          if (res.status === 'ok') mLines.push('  <span style=""color:#0f5132"">✓</span> #' + res.index + ' ' + esc(res.instanceName || '') + ' : ' + esc(res.oldValue || '') + ' → ' + esc(res.newValue || ''));
          else mLines.push('  <span style=""color:#842029"">✗</span> #' + res.index + ' ' + esc(res.instanceName || '') + ' : ' + esc((res.error || '실패').split('\n')[0]));
        });
        mrpResultLog = '<div class=""log"">' + mLines.join('<br>') + '</div>';
      }
    } catch(e) {
      mrpResultLog = '<div class=""log err"">MRP 전체 적용 실패: ' + esc(e.message) + '</div>';
    }
  }

  // 남은 attribute 들은 일반 PUT 으로 처리
  if (changes.length === 0) {
    log.innerHTML = mrpResultLog;
    state.edited = {};
    state.extraEdits = {};
    btn.disabled = true; btn.textContent = 'Execute (수정 0건)';
    var selEl0 = document.querySelector('.tree-node.selected');
    setTimeout(function(){ selectItem(state.selected, selEl0); }, 400);
    return;
  }
  log.innerHTML = '<div class=""log""><span class=""spinner""></span>PUT /api/device/item-attributes 호출 중... (' + changes.length + '건)</div>';
  try {
    var r = await fetch('/api/device/item-attributes', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ project: state.currentProject, device: state.currentDevice, item: state.selected, attributes: changes })
    });
    var data = await r.json();
    var lines = [];
    lines.push('결과: <span class=""ok"">성공 ' + (data.okCount || 0) + '건</span> · <span class=""err"">실패 ' + (data.errCount || 0) + '건</span>');
    (data.results || []).forEach(function(res) {
      if (res.status === 'ok') {
        var mTag = res.method && res.method !== 'SetAttribute' ? ' <span style=""color:#0d6efd"">(' + esc(res.method) + ')</span>' : '';
        lines.push('  <span class=""ok"">✓</span> ' + esc(res.name) + ' : ' + esc(res.oldValue || '') + ' → ' + esc(res.newValue || '') + mTag + (res.targetType ? ' <span style=""color:#888"">[' + esc(res.targetType) + ']</span>' : ''));
      } else if (res.status === 'not_applied') {
        lines.push('  <span style=""color:#b45309;font-weight:600"">⚠</span> ' + esc(res.name) + ' : <span style=""color:#b45309"">값 미반영</span> (기대 ' + esc(res.expectedValue || '') + ', 실제 ' + esc(res.newValue || '') + ')<br>    <span style=""color:#888"">' + esc(res.error || '') + '</span>');
      } else {
        lines.push('  <span class=""err"">✗</span> ' + esc(res.name) + ' : ' + esc(res.error || '실패') + (res.innerError ? ' (' + esc(res.innerError) + ')' : ''));
      }
    });
    log.innerHTML = mrpResultLog + '<div class=""log"">' + lines.join('<br>') + '</div>';
    state.edited = {};

    // Name 변경 등으로 device/item path가 바뀐 경우 state 갱신 후 트리 리로드
    if (data.renamed) {
      if (data.currentDevice) state.currentDevice = data.currentDevice;
      if (data.currentItem) state.selected = data.currentItem;

      // 디바이스 드롭다운 새로 채우기 (currentDevice를 덮어쓰지 않도록 loadDevices 대신 수동)
      try {
        var dr = await api('/api/devices?project=' + encodeURIComponent(state.currentProject || ''));
        state.devices = dr.devices || [];
        var dsel = document.getElementById('sel-device');
        dsel.innerHTML = state.devices.map(function(d){ return '<option value=""' + esc(d.name) + '"">' + esc(d.name) + ' · ' + esc(d.type || '') + '</option>'; }).join('');
        dsel.value = state.currentDevice;
      } catch(_){}

      await loadTree();
      var targetEl = document.querySelector('.tree-node[data-path=""' + state.selected.replace(/""/g, '\\""') + '""]');
      if (targetEl) await selectItem(state.selected, targetEl);
      else {
        // 트리 path 매칭 실패 시 루트 DeviceItem으로 폴백
        var firstEl = document.querySelector('.tree-node');
        if (firstEl) await selectItem(firstEl.getAttribute('data-path'), firstEl);
      }
    } else {
      var selEl = document.querySelector('.tree-node.selected');
      await selectItem(state.selected, selEl);
    }
  } catch(e) {
    log.innerHTML = '<div class=""log err"">실행 실패: ' + esc(e.message) + '</div>';
  } finally {
    var btn2 = document.getElementById('exec-btn');
    if (btn2) { btn2.disabled = Object.keys(state.edited).length === 0; btn2.textContent = 'Execute (수정 ' + Object.keys(state.edited).length + '건)'; }
  }
}

async function applyInterfaceIp() {
  var resultEl = document.getElementById('ip-result');
  if (!state.currentDevice) {
    resultEl.innerHTML = '<span style=""color:#b91c1c"">✗ 디바이스를 선택하세요</span>';
    return;
  }
  var address = (document.getElementById('ip-address').value || '').trim();
  var subnetMask = (document.getElementById('ip-mask').value || '').trim();
  var ifIdxRaw = (document.getElementById('ip-if-idx').value || '').trim();
  if (!address) {
    resultEl.innerHTML = '<span style=""color:#b91c1c"">✗ Address 입력 필요</span>';
    return;
  }

  var body = { device: state.currentDevice, address: address };
  if (state.currentProject) body.project = state.currentProject;
  if (subnetMask) body.subnetMask = subnetMask;
  if (ifIdxRaw !== '') body.interfaceIndex = parseInt(ifIdxRaw, 10);

  var btn = document.getElementById('ip-apply-btn');
  btn.disabled = true;
  resultEl.innerHTML = '<span class=""spinner""></span>PUT /api/device/set-interface-ip ...';
  try {
    var r = await fetch('/api/device/set-interface-ip', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    var data = await r.json();
    if (data.error) {
      resultEl.innerHTML = '<span style=""color:#b91c1c"">✗ ' + esc(data.error) + '</span>';
    } else {
      var before = data.before || {}, after = data.after || {};
      var parts = [];
      parts.push('<span style=""color:' + (data.addressOk ? '#0f5132' : '#b91c1c') + '"">' + (data.addressOk ? '✓' : '✗') + '</span>');
      parts.push('[' + esc(data.interfaceType || '?') + (data.interfaceItem ? ' · ' + esc(data.interfaceItem) : '') + ']');
      parts.push('address: ' + esc(before.address || '<null>') + ' → <strong>' + esc(after.address || '<null>') + '</strong>');
      if (data.maskOk || (data.results && data.results.subnetMask)) {
        parts.push('· mask: ' + esc(before.subnetMask || '<null>') + ' → <strong>' + esc(after.subnetMask || '<null>') + '</strong>');
      }
      if (data.results && data.results.addressError) parts.push('<span style=""color:#b91c1c"">addressErr: ' + esc(data.results.addressError) + '</span>');
      if (data.results && data.results.subnetMaskError) parts.push('<span style=""color:#b91c1c"">maskErr: ' + esc(data.results.subnetMaskError) + '</span>');
      resultEl.innerHTML = parts.join(' ');
    }
  } catch(e) {
    resultEl.innerHTML = '<span style=""color:#b91c1c"">✗ ' + esc(e.message) + '</span>';
  } finally {
    btn.disabled = false;
  }
}

document.getElementById('sel-project').onchange = async function() { state.currentProject = this.value; await loadDevices(); };
document.getElementById('sel-device').onchange = async function() { state.currentDevice = this.value; await loadTree(); };

loadProjects();
</script>
</body>
</html>";
            resp.StatusCode = 200;
            resp.ContentType = "text/html; charset=utf-8";
            byte[] buf = Encoding.UTF8.GetBytes(html);
            resp.ContentLength64 = buf.Length;
            resp.OutputStream.Write(buf, 0, buf.Length);
            resp.Close();
        }

        static void ServeNetworkPropertiesUI(HttpListenerResponse resp)
        {
            string html = @"<!DOCTYPE html>
<html lang=""ko"">
<head>
<meta charset=""UTF-8"">
<title>네트워크 커넥션 편집 · TIA Openness</title>
<style>
* { box-sizing: border-box; }
body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; margin: 0; height: 100vh; display: flex; flex-direction: column; font-size: 13px; color: #222; background: white; }
.toolbar { background: #1b1b1b; color: white; padding: 10px 16px; display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
.toolbar strong { font-size: 14px; }
.toolbar a { color: #79c0ff; text-decoration: none; font-size: 12px; }
.toolbar a:hover { text-decoration: underline; }
.toolbar label { font-size: 12px; color: #bbb; }
.toolbar select, .toolbar button { padding: 5px 10px; font-size: 13px; border-radius: 3px; border: 1px solid #444; background: #2a2a2a; color: #eee; }
.toolbar button { background: #0d6efd; color: white; border: none; cursor: pointer; }
.toolbar button:hover { background: #0b5ed7; }
.toolbar .status { margin-left: auto; font-size: 12px; color: #aaa; }
.main { flex: 1; display: flex; flex-direction: column; overflow: hidden; min-height: 0; }
.content-body { padding: 14px 20px; flex: 1; overflow: auto; }
.legend { display: flex; gap: 12px; font-size: 11px; color: #666; margin: 6px 0 12px 0; flex-wrap: wrap; }
.legend span { display: inline-flex; align-items: center; gap: 4px; }
.panel-header { font-weight: 600; font-size: 15px; padding: 4px 0 10px 0; border-bottom: 1px solid #ddd; margin-bottom: 12px; display: flex; justify-content: space-between; align-items: baseline; }
.panel-header .count { font-weight: normal; color: #888; font-size: 12px; }
table.if-table { width: 100%; border-collapse: collapse; font-size: 12px; }
table.if-table th, table.if-table td { padding: 6px 8px; border-bottom: 1px solid #eee; text-align: left; vertical-align: middle; }
table.if-table th { background: #f8f9fa; font-weight: 600; position: sticky; top: 0; border-bottom: 2px solid #dee2e6; }
table.if-table td.device { font-weight: 600; color: #333; }
table.if-table td.if-name { color: #555; font-family: 'Consolas', monospace; word-break: break-all; }
table.if-table td.path { color: #888; font-family: 'Consolas', monospace; font-size: 11px; }
table.if-table input { width: 100%; padding: 3px 6px; font-size: 12px; font-family: 'Consolas', monospace; border: 1px solid #ccc; border-radius: 2px; }
table.if-table input:focus { outline: none; border-color: #0d6efd; box-shadow: 0 0 0 2px #0d6efd33; }
table.if-table input.changed { background: #fff3cd; border-color: #ffc107; }
table.if-table tr:hover { background: #f9fbff; }
.badge-type { display: inline-block; padding: 2px 8px; border-radius: 10px; font-size: 10px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.4px; }
.badge-ethernet { background: #d1e7dd; color: #0f5132; }
.badge-profibus { background: #ffe5b4; color: #8a4b00; }
.badge-mpi      { background: #e2d9f3; color: #432874; }
.badge-other    { background: #e9ecef; color: #495057; }
button.apply-btn { background: #198754; color: white; border: none; padding: 5px 12px; border-radius: 3px; cursor: pointer; font-size: 12px; font-weight: 600; }
button.apply-btn:hover:not(:disabled) { background: #157347; }
button.apply-btn:disabled { background: #6c757d; cursor: not-allowed; opacity: 0.7; }
.row-msg { font-size: 11px; margin-top: 4px; font-family: 'Consolas', monospace; }
.row-msg.ok { color: #0f5132; }
.row-msg.err { color: #842029; font-weight: 600; }
.empty { padding: 30px; color: #888; text-align: center; font-size: 13px; }
.spinner { display: inline-block; width: 12px; height: 12px; border: 2px solid #ccc; border-top-color: #0d6efd; border-radius: 50%; animation: spin 0.6s linear infinite; vertical-align: -2px; margin-right: 6px; }
@keyframes spin { to { transform: rotate(360deg); } }
.hint { color: #888; font-size: 12px; margin-top: 6px; }
.subnet-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: 12px; margin-bottom: 18px; }
.subnet-card { border: 1px solid #d6e4f0; border-radius: 6px; background: linear-gradient(180deg, #f7fbff 0%, #ffffff 70%); padding: 10px 12px; }
.subnet-card-header { display: flex; align-items: center; gap: 6px; padding-bottom: 6px; border-bottom: 1px solid #e2ecf5; margin-bottom: 8px; }
.subnet-card-body { display: flex; flex-direction: column; gap: 4px; }
.subnet-card .mini-label { color: #888; font-size: 10px; text-transform: uppercase; letter-spacing: 0.5px; }
.subnet-card .rename-row { display: flex; gap: 6px; align-items: center; }
.subnet-card input { flex: 1; padding: 4px 8px; font-size: 12px; font-family: 'Consolas', monospace; border: 1px solid #cfd8e3; border-radius: 3px; }
.subnet-card input:focus { outline: none; border-color: #0d6efd; box-shadow: 0 0 0 2px #0d6efd33; }
button.mini-btn { background: #0d6efd; color: white; border: none; padding: 4px 10px; border-radius: 3px; cursor: pointer; font-size: 11px; font-weight: 600; }
button.mini-btn:hover { background: #0b5ed7; }
.group-section { margin-top: 18px; border: 1px solid #e0e6ed; border-radius: 6px; overflow: hidden; }
.group-header { background: #eef6ff; padding: 8px 14px; font-weight: 600; color: #084c84; border-bottom: 1px solid #cfe2ff; display: flex; align-items: center; gap: 6px; }
.group-header.danger { background: #fff7e6; color: #8a4b00; border-bottom-color: #ffd591; }
.group-section table.if-table th { background: transparent; }
table.if-table select { width: 100%; padding: 3px 6px; font-size: 12px; font-family: 'Consolas', monospace; border: 1px solid #ccc; border-radius: 2px; background: white; }
table.if-table select:focus { outline: none; border-color: #0d6efd; }
</style>
</head>
<body>
<div class=""toolbar"">
  <strong>🌐 네트워크 커넥션 편집 (Subnets · Subnet Configs)</strong>
  <a href=""/"">← Swagger UI</a>
  <a href=""/ui/device-properties"">⚙️ CPU 속성 편집</a>
  <span style=""margin-left:20px;width:1px;height:20px;background:#444""></span>
  <label>프로젝트</label>
  <select id=""sel-project""></select>
  <button onclick=""loadAll()"">🔄 새로고침</button>
  <span class=""status"" id=""conn-status""></span>
</div>
<div class=""main"">
  <div class=""content-body"" id=""content"">
    <div class=""hint"">TIA Network View 와 동일한 관점 — 프로젝트 내 모든 Subnet 과 각 Subnet 에 연결된 인터페이스(IP/Mask/PROFINET Device Name) 를 그룹별로 보고 편집합니다.</div>
    <div class=""hint"">Subnet Name 변경 · 인터페이스의 Subnet 연결/해제/이동 · 새 Subnet 생성 · IP / Subnet Mask / PROFINET Device Name 수정 가능</div>
    <div class=""empty"" id=""empty-msg"">프로젝트를 선택하세요</div>
  </div>
</div>

<script>
var state = { projects: [], currentProject: null, subnets: [], unattached: [], openedSubnet: null, subnetAttrs: [], subnetAttrEdits: {} };

function esc(s) { return String(s == null ? '' : s).replace(/[&<>""']/g, function(c){ return {'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;',""'"":'&#39;'}[c]; }); }
function setStatus(t) { document.getElementById('conn-status').textContent = t; }
function typeBadge(t) {
  var cls = 'badge-other';
  if (!t) t = '?';
  var tl = t.toLowerCase();
  if (tl.indexOf('ethernet') >= 0) cls = 'badge-ethernet';
  else if (tl.indexOf('profibus') >= 0) cls = 'badge-profibus';
  else if (tl.indexOf('mpi') >= 0) cls = 'badge-mpi';
  return '<span class=""badge-type ' + cls + '"">' + esc(t) + '</span>';
}

async function api(url, opts) {
  var r = await fetch(url, opts);
  var data;
  try { data = await r.json(); } catch(e) { throw new Error('JSON 파싱 실패 · HTTP ' + r.status); }
  if (!r.ok || data.error) throw new Error(data.error || ('HTTP ' + r.status));
  return data;
}

async function loadProjects() {
  setStatus('프로젝트 로딩...');
  try {
    var data = await api('/api/projects');
    state.projects = data.projects || [];
    var sel = document.getElementById('sel-project');
    if (!state.projects.length) {
      sel.innerHTML = '<option value="""">(없음)</option>';
      setStatus('프로젝트 없음 · /api/connect 먼저 호출');
      document.getElementById('empty-msg').textContent = 'TIA Portal 에 연결되지 않았습니다';
      return;
    }
    sel.innerHTML = state.projects.map(function(p){ return '<option value=""' + esc(p.name) + '"">' + esc(p.name) + '</option>'; }).join('');
    state.currentProject = state.projects[0].name;
    setStatus('프로젝트 ' + state.projects.length + '개');
    await loadAll();
  } catch(e) {
    setStatus('오류: ' + e.message);
    document.getElementById('empty-msg').textContent = '연결 실패: ' + e.message;
  }
}

async function loadAll() {
  if (!state.currentProject) return;
  var content = document.getElementById('content');
  content.innerHTML = '<div class=""empty""><span class=""spinner""></span>Subnet · 인터페이스 로딩...</div>';
  try {
    var data = await api('/api/network/subnets?project=' + encodeURIComponent(state.currentProject));
    state.subnets = data.subnets || [];
    state.unattached = data.unattached || [];
    render();
  } catch(e) {
    content.innerHTML = '<div class=""empty"">로딩 실패: ' + esc(e.message) + '</div>';
  }
}

function render() {
  var totalNodes = state.subnets.reduce(function(s, sn){ return s + ((sn.nodes||[]).length); }, 0);
  var html = '<div class=""panel-header""><span>📡 Subnets</span><span class=""count"">총 ' + state.subnets.length + '개 · 연결 ' + totalNodes + ' · 미연결 ' + state.unattached.length + '</span></div>';

  if (state.subnets.length === 0) {
    html += '<div class=""empty"" style=""text-align:left;padding:14px"">이 프로젝트에 Subnet 이 없습니다. 아래 <strong>미연결 인터페이스</strong> 의 Subnet 드롭다운에서 <strong>+ 새 Subnet 생성</strong> 을 선택해 만들 수 있습니다.</div>';
  } else {
    html += '<div class=""subnet-grid"">';
    state.subnets.forEach(function(sn, i) {
      var nodeCount = (sn.nodes || []).length;
      html += '<div class=""subnet-card"">';
      html += '<div class=""subnet-card-header"">' + typeBadge(sn.type || 'Subnet') + ' <strong style=""color:#084c84;font-size:13px"">' + esc(sn.name) + '</strong><span style=""margin-left:auto;color:#888;font-size:11px"">' + nodeCount + ' nodes</span></div>';
      html += '<div class=""subnet-card-body"">';
      html += '<span class=""mini-label"">Subnet Name (rename)</span>';
      html += '<div class=""rename-row"">';
      html += '<input type=""text"" id=""sn-name-' + i + '"" value=""' + esc(sn.name) + '"" data-orig=""' + esc(sn.name) + '"">';
      html += '<button class=""mini-btn"" onclick=""renameSubnet(' + i + ')"">Rename</button>';
      html += '</div>';
      html += '<div style=""display:flex;gap:6px;margin-top:6px"">';
      html += '<button class=""mini-btn"" style=""background:#6f42c1"" onclick=""openSubnetProps(' + esc(JSON.stringify(sn.name)) + ')"">⚙ Properties (Transmission speed · Profile · Bus parameters)</button>';
      html += '</div>';
      html += '<div class=""row-msg"" id=""sn-msg-' + i + '""></div>';
      html += '</div></div>';
    });
    html += '</div>';
  }

  // Subnet Properties 인라인 패널
  if (state.openedSubnet) {
    html += renderSubnetPropsPanel();
  }

  state.subnets.forEach(function(sn, i) {
    var nodes = sn.nodes || [];
    if (nodes.length === 0) return;
    html += '<div class=""group-section"">';
    html += '<div class=""group-header"">' + typeBadge(sn.type || 'Subnet') + ' <strong>' + esc(sn.name) + '</strong> <span style=""color:#888;font-weight:normal;margin-left:6px"">· ' + nodes.length + ' nodes</span></div>';
    html += renderNodesTable(nodes, sn.name, 'sn' + i);
    html += '</div>';
  });

  if (state.unattached.length > 0) {
    html += '<div class=""group-section"">';
    html += '<div class=""group-header danger"">⚠ 미연결 인터페이스 (Subnet 없음) <span style=""color:#888;font-weight:normal;margin-left:6px"">· ' + state.unattached.length + ' nodes</span></div>';
    html += renderNodesTable(state.unattached, null, 'un');
    html += '</div>';
  }

  document.getElementById('content').innerHTML = html;
}

function renderNodesTable(nodes, currentSubnetName, prefix) {
  var rows = nodes.map(function(it, idx) {
    var key = prefix + '-' + idx;
    var isEth = (it.interfaceType || '').toLowerCase().indexOf('ethernet') >= 0;
    var addrPh = isEth ? '192.168.0.10' : 'Station#';
    var maskField = isEth
      ? '<input type=""text"" id=""' + key + '-mask"" value=""' + esc(it.subnetMask || '') + '"" placeholder=""255.255.255.0"">'
      : '<span style=""color:#bbb"">N/A</span>';
    var pnNameField = isEth
      ? '<input type=""text"" id=""' + key + '-pnname"" value=""' + esc(it.profinetDeviceName || '') + '"" placeholder=""profinet device name"">'
      : '<span style=""color:#bbb"">N/A</span>';

    var subnetOpts = '';
    if (currentSubnetName) subnetOpts += '<option value="""">(미연결로 두기)</option>';
    else subnetOpts += '<option value="""" selected>(미연결)</option>';
    state.subnets.forEach(function(sn){
      var sel = sn.name === currentSubnetName ? ' selected' : '';
      subnetOpts += '<option value=""' + esc(sn.name) + '""' + sel + '>' + esc(sn.name) + ' [' + esc(sn.type || '?') + ']</option>';
    });
    if (currentSubnetName) subnetOpts += '<option value=""__detach__"">— 현재 Subnet 에서 분리 —</option>';
    subnetOpts += '<option value=""__new__"">+ 새 Subnet 생성...</option>';

    var dataOrig = currentSubnetName || '';
    var devArg = esc(JSON.stringify(it.device));
    var keyArg = esc(JSON.stringify(key));

    return '<tr>'
      + '<td class=""device"">' + esc(it.device) + '</td>'
      + '<td>' + typeBadge(it.interfaceType) + '</td>'
      + '<td class=""if-name"" title=""' + esc(it.interfaceItemPath || '') + '"">' + esc(it.interfaceName) + '</td>'
      + '<td><input type=""text"" id=""' + key + '-addr"" value=""' + esc(it.address || '') + '"" placeholder=""' + esc(addrPh) + '""></td>'
      + '<td>' + maskField + '</td>'
      + '<td>' + pnNameField + '</td>'
      + '<td><select id=""' + key + '-subnet"" data-orig=""' + esc(dataOrig) + '"">' + subnetOpts + '</select></td>'
      + '<td><button class=""apply-btn"" onclick=""applyRow(' + devArg + ',' + it.interfaceIndex + ',' + keyArg + ')"">Apply</button><div class=""row-msg"" id=""' + key + '-msg""></div></td>'
      + '</tr>';
  }).join('');

  return '<table class=""if-table""><thead><tr>'
    + '<th>Device</th><th>Type</th><th>Interface</th><th>Address</th><th>Subnet Mask</th><th>PROFINET Device Name</th><th>Subnet</th><th style=""width:130px"">Apply</th>'
    + '</tr></thead><tbody>' + rows + '</tbody></table>';
}

function findInterface(device, interfaceIndex) {
  for (var i = 0; i < state.subnets.length; i++) {
    var nodes = state.subnets[i].nodes || [];
    for (var j = 0; j < nodes.length; j++) {
      if (nodes[j].device === device && nodes[j].interfaceIndex === interfaceIndex) return nodes[j];
    }
  }
  for (var k = 0; k < state.unattached.length; k++) {
    if (state.unattached[k].device === device && state.unattached[k].interfaceIndex === interfaceIndex) return state.unattached[k];
  }
  return null;
}

async function applyRow(device, interfaceIndex, key) {
  var msg = document.getElementById(key + '-msg');
  msg.className = 'row-msg';
  msg.innerHTML = '<span class=""spinner""></span>전송 중...';

  var addrEl = document.getElementById(key + '-addr');
  var maskEl = document.getElementById(key + '-mask');
  var pnEl = document.getElementById(key + '-pnname');
  var subnetEl = document.getElementById(key + '-subnet');
  var it = findInterface(device, interfaceIndex);

  var logs = []; var anyError = false;

  // 1) Subnet 연결/해제/이동 (먼저 — Subnet 상태가 IP 설정 가용성에 영향을 줄 수 있음)
  if (subnetEl) {
    var orig = subnetEl.getAttribute('data-orig') || '';
    var sel = subnetEl.value;
    if (sel !== orig) {
      try {
        if (sel === '__detach__' || (sel === '' && orig !== '')) {
          var r = await fetch('/api/network/detach', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ project: state.currentProject, device: device, interfaceIndex: interfaceIndex }) });
          var d = await r.json();
          if (d.error) { anyError = true; logs.push('✗ Detach: ' + esc(d.error)); }
          else logs.push('✓ Subnet: ' + esc(orig || '<없음>') + ' → <분리됨>');
        } else if (sel === '__new__') {
          var defaultName = (it && (it.interfaceType || '').toLowerCase().indexOf('profibus') >= 0) ? 'DP_' + (state.subnets.length + 1) : 'PN/IE_' + (state.subnets.length + 1);
          var newName = prompt('생성할 Subnet 이름', defaultName);
          if (newName && newName.trim()) {
            var rN = await fetch('/api/network/attach', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ project: state.currentProject, device: device, interfaceIndex: interfaceIndex, subnetName: newName.trim(), createIfMissing: true }) });
            var dN = await rN.json();
            if (dN.error) { anyError = true; logs.push('✗ Create+Attach: ' + esc(dN.error)); }
            else logs.push('✓ Subnet 생성+연결: ' + esc(dN.connectedSubnet || newName));
          } else { logs.push('· 새 Subnet 생성 취소'); }
        } else if (sel !== '') {
          var rA = await fetch('/api/network/attach', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ project: state.currentProject, device: device, interfaceIndex: interfaceIndex, subnetName: sel, createIfMissing: false }) });
          var dA = await rA.json();
          if (dA.error) { anyError = true; logs.push('✗ Attach: ' + esc(dA.error)); }
          else logs.push('✓ Subnet: ' + esc(orig || '<없음>') + ' → ' + esc(dA.connectedSubnet || sel));
        }
      } catch(e) { anyError = true; logs.push('✗ Subnet: ' + esc(e.message)); }
    }
  }

  // 2) Address / SubnetMask via set-interface-ip
  if (addrEl) {
    var newAddr = addrEl.value.trim();
    var newMask = maskEl ? maskEl.value.trim() : '';
    var origAddr = (it && it.address) || '';
    var origMask = (it && it.subnetMask) || '';
    if (newAddr !== origAddr || (maskEl && newMask !== origMask)) {
      var body = { project: state.currentProject, device: device, interfaceIndex: interfaceIndex, address: newAddr };
      if (maskEl && newMask) body.subnetMask = newMask;
      try {
        var rI = await fetch('/api/device/set-interface-ip', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
        var dI = await rI.json();
        if (dI.error) { anyError = true; logs.push('✗ IP: ' + esc(dI.error)); }
        else {
          var a = (dI.after && dI.after.address) || '';
          var m = (dI.after && dI.after.subnetMask) || '';
          logs.push('✓ Address=' + esc(a) + (m ? ' Mask=' + esc(m) : ''));
        }
      } catch(e) { anyError = true; logs.push('✗ IP: ' + esc(e.message)); }
    }
  }

  // 3) ProfinetDeviceName via item-attributes
  if (pnEl && it) {
    var newPn = pnEl.value;
    var origPn = it.profinetDeviceName || '';
    if (newPn !== origPn) {
      var attrBody = { project: state.currentProject, device: device, item: it.interfaceItemPath, attributes: [{ name: 'ProfinetDeviceName', value: newPn }] };
      try {
        var rP = await fetch('/api/device/item-attributes', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(attrBody) });
        var dP = await rP.json();
        var res = (dP.results && dP.results[0]) || {};
        if (res.status === 'ok') logs.push('✓ ProfinetDeviceName=' + esc(res.newValue || ''));
        else { anyError = true; logs.push('✗ ProfinetDeviceName: ' + esc(res.error || dP.error || '실패')); }
      } catch(e) { anyError = true; logs.push('✗ ProfinetDeviceName: ' + esc(e.message)); }
    }
  }

  if (logs.length === 0) logs.push('· 변경 사항 없음');
  msg.className = 'row-msg ' + (anyError ? 'err' : 'ok');
  msg.innerHTML = logs.join('<br>');

  setTimeout(loadAll, 700);
}

// Enum 값 → 사람 읽기 좋은 라벨 (예: Baud1500000 → 1.5 Mbps)
function enumFriendlyLabel(ev) {
  if (!ev) return '';
  // Baud<digits> → bit/s
  var m = /^Baud(\d+)$/.exec(ev);
  if (m) {
    var n = parseInt(m[1], 10);
    if (n >= 1000000) return (n / 1000000).toString().replace(/\.0$/, '') + ' Mbps';
    if (n >= 1000) return (n / 1000).toString().replace(/\.0$/, '') + ' kbps';
    return n + ' bps';
  }
  // PascalCase 약간 정리 (예: DpV1 → DP V1, ProfileDp → Profile DP)
  return ev.replace(/([a-z])([A-Z])/g, '$1 $2');
}

async function openSubnetProps(name) {
  state.openedSubnet = name;
  state.subnetAttrs = [];
  state.subnetAttrEdits = {};
  render();
  try {
    var data = await api('/api/network/subnet/attributes?project=' + encodeURIComponent(state.currentProject) + '&name=' + encodeURIComponent(name));
    state.subnetAttrs = data.attributes || [];
    render();
  } catch(e) {
    state.subnetAttrs = [{ name: '_error', value: e.message, accessMode: 'Read' }];
    render();
  }
}

function closeSubnetProps() {
  state.openedSubnet = null;
  state.subnetAttrs = [];
  state.subnetAttrEdits = {};
  render();
}

function renderSubnetPropsPanel() {
  var name = state.openedSubnet;
  var attrs = state.subnetAttrs || [];
  var loading = attrs.length === 0;

  var html = '<div class=""group-section"" style=""border-color:#6f42c1aa"">';
  html += '<div class=""group-header"" style=""background:#f3eaff;color:#432874;border-bottom-color:#d9c4f0;display:flex;align-items:center"">';
  html += '<span>⚙ Subnet Properties · <strong>' + esc(name) + '</strong></span>';
  html += '<span style=""margin-left:auto;display:flex;gap:6px"">';
  html += '<button class=""mini-btn"" style=""background:#198754"" onclick=""saveSubnetProps()"">💾 Save Changes</button>';
  html += '<button class=""mini-btn"" style=""background:#6c757d"" onclick=""closeSubnetProps()"">✕ Close</button>';
  html += '</span></div>';

  if (loading) {
    html += '<div class=""empty""><span class=""spinner""></span>속성 로딩...</div></div>';
    return html;
  }

  var rw = attrs.filter(function(a){ return a.accessMode !== 'Read'; }).length;
  var ro = attrs.length - rw;
  html += '<div style=""padding:8px 14px;color:#666;font-size:12px"">총 ' + attrs.length + '개 · RW ' + rw + ' · 읽기전용 ' + ro + ' · <span style=""color:#6f42c1"">★ PROFIBUS 주요</span> Transmission speed / Profile / Highest address</div>';

  var rows = attrs.map(function(a, i) {
    var isRO = (a.accessMode === 'Read');
    var val = a.value == null ? '' : a.value;
    var typeTag = a.type ? '<span style=""color:#888;font-size:11px;margin-left:6px"">[' + esc(a.type) + (a.isEnum ? ' · enum' : '') + ']</span>' : '';
    var amTag = isRO ? '<span class=""badge-type badge-other"" style=""font-size:9px"">R</span>' : '<span class=""badge-type"" style=""background:#d1e7dd;color:#0f5132;font-size:9px"">RW</span>';
    var nameLower = (a.name || '').toLowerCase();
    var highlight = (nameLower.indexOf('transmission') >= 0 || nameLower.indexOf('profile') >= 0 || nameLower.indexOf('highest') >= 0 || nameLower.indexOf('baud') >= 0 || nameLower === 'speed') ? ' style=""background:#fdf6ff""' : '';
    var star = highlight ? ' <span style=""color:#6f42c1"" title=""PROFIBUS Network settings 핵심"">★</span>' : '';

    // 편집 입력 위젯: enum 이면 select, 아니면 text input
    var editCell;
    if (isRO) {
      editCell = '<input type=""text"" value=""' + esc(val) + '"" readonly tabindex=""-1"" style=""width:100%;padding:3px 6px;font-size:12px;font-family:Consolas,monospace;border:1px solid #ccc;border-radius:2px;background:#f1f3f5;color:#888"">';
    } else if (a.isEnum && a.enumValues && a.enumValues.length) {
      var opts = a.enumValues.map(function(ev){
        var label = enumFriendlyLabel(ev);
        var sel = (ev === val) ? ' selected' : '';
        return '<option value=""' + esc(ev) + '""' + sel + '>' + esc(ev) + (label && label !== ev ? ' — ' + esc(label) : '') + '</option>';
      }).join('');
      // 만약 현재 값이 enumValues 리스트에 없으면 첫 옵션으로 (예외 케이스)
      editCell = '<select onchange=""markSubnetAttrEdit(' + i + ', this.value)"" style=""width:100%;padding:3px 6px;font-size:12px;font-family:Consolas,monospace;border:1px solid #ccc;border-radius:2px;background:white"">' + opts + '</select>';
    } else {
      editCell = '<input type=""text"" value=""' + esc(val) + '"" oninput=""markSubnetAttrEdit(' + i + ', this.value)"" style=""width:100%;padding:3px 6px;font-size:12px;font-family:Consolas,monospace;border:1px solid #ccc;border-radius:2px"">';
    }

    var displayVal = val;
    if (a.isEnum) {
      var lbl = enumFriendlyLabel(val);
      if (lbl && lbl !== val) displayVal = val + ' (' + lbl + ')';
    }

    return '<tr' + highlight + ' data-attr-idx=""' + i + '"">'
      + '<td style=""width:30%""><strong>' + esc(a.name) + '</strong>' + star + typeTag + '</td>'
      + '<td style=""width:6%;text-align:center"">' + amTag + '</td>'
      + '<td style=""width:30%;color:#555;font-family:Consolas,monospace;font-size:12px;word-break:break-all"">' + esc(displayVal === '' ? '<null>' : displayVal) + '</td>'
      + '<td style=""width:34%"">' + editCell + '</td>'
      + '</tr>';
  }).join('');

  html += '<table class=""if-table""><thead><tr><th>Name / Type</th><th style=""text-align:center"">Mode</th><th>현재 값</th><th>새 값</th></tr></thead><tbody>' + rows + '</tbody></table>';
  html += '<div class=""row-msg"" id=""subnet-props-msg"" style=""padding:8px 14px""></div>';
  html += '</div>';
  return html;
}

function markSubnetAttrEdit(i, newVal) {
  var a = state.subnetAttrs[i];
  if (!a || a.accessMode === 'Read') return;
  var oldVal = a.value == null ? '' : String(a.value);
  if (String(newVal) !== oldVal) state.subnetAttrEdits[a.name] = newVal;
  else delete state.subnetAttrEdits[a.name];
}

async function saveSubnetProps() {
  var msg = document.getElementById('subnet-props-msg');
  var changes = Object.keys(state.subnetAttrEdits).map(function(k){ return { name: k, value: state.subnetAttrEdits[k] }; });
  if (!changes.length) { msg.className = 'row-msg'; msg.innerHTML = '<span style=""color:#888"">변경 사항 없음</span>'; return; }
  msg.className = 'row-msg';
  msg.innerHTML = '<span class=""spinner""></span>PUT /api/network/subnet/attributes 호출 중... (' + changes.length + '건)';
  try {
    var r = await fetch('/api/network/subnet/attributes', {
      method: 'PUT', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ project: state.currentProject, name: state.openedSubnet, attributes: changes })
    });
    var d = await r.json();
    var lines = ['결과: <span style=""color:#0f5132"">성공 ' + (d.okCount || 0) + '</span> · <span style=""color:#842029"">실패 ' + (d.errCount || 0) + '</span>'];
    (d.results || []).forEach(function(res){
      if (res.status === 'ok') lines.push('  <span style=""color:#0f5132"">✓</span> ' + esc(res.name) + ' : ' + esc(res.oldValue || '') + ' → ' + esc(res.newValue || ''));
      else lines.push('  <span style=""color:#842029"">✗</span> ' + esc(res.name) + ' : ' + esc(res.error || '실패') + (res.innerError ? ' (' + esc(res.innerError) + ')' : ''));
    });
    msg.className = 'row-msg';
    msg.innerHTML = lines.join('<br>');
    state.subnetAttrEdits = {};
    // 속성 재로딩
    try {
      var dd = await api('/api/network/subnet/attributes?project=' + encodeURIComponent(state.currentProject) + '&name=' + encodeURIComponent(state.openedSubnet));
      state.subnetAttrs = dd.attributes || [];
      render();
      // 메시지 다시 표시 (render 가 패널을 재생성하므로)
      var msg2 = document.getElementById('subnet-props-msg');
      if (msg2) msg2.innerHTML = lines.join('<br>');
    } catch(_) {}
  } catch(e) {
    msg.className = 'row-msg err';
    msg.innerHTML = '✗ ' + esc(e.message);
  }
}

async function renameSubnet(i) {
  var inp = document.getElementById('sn-name-' + i);
  var msg = document.getElementById('sn-msg-' + i);
  var orig = inp.getAttribute('data-orig') || '';
  var newName = inp.value.trim();
  if (!newName) { msg.className = 'row-msg err'; msg.innerHTML = '✗ 이름이 비어있음'; return; }
  if (newName === orig) { msg.className = 'row-msg'; msg.innerHTML = '<span style=""color:#888"">변경 없음</span>'; return; }
  msg.className = 'row-msg';
  msg.innerHTML = '<span class=""spinner""></span>Rename...';
  try {
    var r = await fetch('/api/network/subnet/rename', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ project: state.currentProject, oldName: orig, newName: newName }) });
    var d = await r.json();
    if (d.error) { msg.className = 'row-msg err'; msg.innerHTML = '✗ ' + esc(d.error); }
    else { msg.className = 'row-msg ok'; msg.innerHTML = '✓ ' + esc(d.before) + ' → ' + esc(d.after); setTimeout(loadAll, 700); }
  } catch(e) { msg.className = 'row-msg err'; msg.innerHTML = '✗ ' + esc(e.message); }
}

document.getElementById('sel-project').onchange = async function() { state.currentProject = this.value; await loadAll(); };

loadProjects();
</script>
</body>
</html>";
            resp.StatusCode = 200;
            resp.ContentType = "text/html; charset=utf-8";
            byte[] buf = Encoding.UTF8.GetBytes(html);
            resp.ContentLength64 = buf.Length;
            resp.OutputStream.Write(buf, 0, buf.Length);
            resp.Close();
        }

        static void ServeExplorerUI(HttpListenerResponse resp)
        {
            string html = @"<!DOCTYPE html>
<html lang='ko'>
<head>
<meta charset='UTF-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>CPU Explorer - TIA Portal Openness</title>
<style>
  :root{--bg:#f6f7f9;--fg:#1b1d20;--mut:#6b7280;--line:#dfe3e8;--card:#fff;--acc:#2563eb;--ok:#16a34a;--warn:#b45309}
  @media (prefers-color-scheme:dark){:root{--bg:#15171a;--fg:#e7e9ec;--mut:#9aa3af;--line:#2b2f36;--card:#1b1e22;--acc:#60a5fa;--ok:#4ade80;--warn:#fbbf24}}
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--fg);font:13px/1.5 -apple-system,Segoe UI,Roboto,'Malgun Gothic',sans-serif}
  header{padding:14px 18px;border-bottom:1px solid var(--line);background:var(--card);position:sticky;top:0;z-index:5}
  h1{margin:0 0 10px;font-size:15px;font-weight:600}
  .row{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
  input[type=text]{padding:6px 9px;border:1px solid var(--line);border-radius:6px;background:var(--bg);color:var(--fg);font:inherit}
  button{padding:6px 12px;border:1px solid var(--line);border-radius:6px;background:var(--card);color:var(--fg);font:inherit;cursor:pointer}
  button:hover{border-color:var(--acc)}
  button.primary{background:var(--acc);border-color:var(--acc);color:#fff}
  button:disabled{opacity:.45;cursor:not-allowed}
  .mut{color:var(--mut)}
  main{padding:14px 18px}
  ul{list-style:none;margin:0;padding-left:17px}
  ul.root{padding-left:0}
  li{margin:1px 0}
  .node{display:flex;align-items:center;gap:6px;padding:1px 3px;border-radius:4px}
  .node:hover{background:rgba(127,127,127,.09)}
  .tw{width:14px;text-align:center;cursor:pointer;color:var(--mut);user-select:none;flex:none}
  .tw.leaf{cursor:default;opacity:.3}
  .nm{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
  .tag{font-size:10px;padding:0 5px;border-radius:9px;border:1px solid var(--line);color:var(--mut);flex:none}
  .bad{color:var(--warn);border-color:var(--warn)}
  .hide{display:none}
  #status{margin-top:9px;font-size:12px;color:var(--mut)}
  .folder>.node>.nm{font-weight:600}
</style>
</head>
<body>
<header>
  <h1>CPU Explorer <span class='mut'>체크한 XML 만 골라서 export</span></h1>
  <div class='row'>
    <input type='text' id='plc' list='devs' placeholder='PLC 이름 (예: PLC_1)' size='22'>
    <datalist id='devs'></datalist>
    <button id='load'>트리 불러오기</button>
    <input type='text' id='filter' placeholder='이름 필터' size='18'>
    <button id='all'>전체 선택</button>
    <button id='none'>해제</button>
    <label class='mut'><input type='checkbox' id='compile'> export 전 compile</label>
    <button id='go' class='primary' disabled>선택 Export (0)</button>
  </div>
  <div id='status' class='mut'>TIA Portal 에 연결된 상태여야 합니다. 먼저 /api/connect 를 호출하세요.</div>
</header>
<main><div id='tree' class='mut'>아직 불러오지 않았습니다.</div></main>
<script>
var LEAVES = [];
function el(t,c){var e=document.createElement(t); if(c) e.className=c; return e;}
function setStatus(m,bad){var s=document.getElementById('status'); s.textContent=m; s.style.color = bad ? 'var(--warn)' : 'var(--mut)';}

fetch('/api/devices').then(function(r){return r.json();}).then(function(d){
  var dl=document.getElementById('devs'); (d.devices||[]).forEach(function(x){
    if(!x || !x.name) return; var o=el('option'); o.value=x.name; dl.appendChild(o);});
}).catch(function(){});

function build(nodes, parentUl){
  nodes.forEach(function(n){
    var li=el('li', n.kind==='folder' ? 'folder' : 'leaf');
    var row=el('div','node');
    var tw=el('span','tw'+(n.kind==='folder'?'':' leaf'));
    tw.textContent = n.kind==='folder' ? '▾' : '·';
    var cb=el('input'); cb.type='checkbox';
    var nm=el('span','nm'); nm.textContent=n.name;
    row.appendChild(tw); row.appendChild(cb); row.appendChild(nm);
    if(n.info && n.info.type){
      var t=el('span','tag'); t.textContent=n.info.type + (n.info.number!=null ? ' #'+n.info.number : '');
      row.appendChild(t);
      if(n.info.consistent===false){var w=el('span','tag bad'); w.textContent='inconsistent'; row.appendChild(w);}
    }
    li.appendChild(row);
    if(n.kind==='folder'){
      var ul=el('ul'); build(n.children||[], ul); li.appendChild(ul);
      tw.onclick=function(){var h=ul.classList.toggle('hide'); tw.textContent=h?'▸':'▾';};
      cb.onchange=function(){ul.querySelectorAll('input[type=checkbox]').forEach(function(c){c.checked=cb.checked;c.indeterminate=false;}); up(li); count();};
    } else {
      li.dataset.path=n.path; li.dataset.name=n.name.toLowerCase();
      LEAVES.push(li);
      cb.onchange=function(){up(li); count();};
    }
    parentUl.appendChild(li);
  });
}
function up(li){
  var pul=li.parentElement; if(!pul) return;
  var pli=pul.closest('li.folder'); if(!pli) return;
  var kids=Array.prototype.slice.call(pul.children).map(function(c){return c.querySelector(':scope > .node > input');}).filter(Boolean);
  var on=kids.filter(function(c){return c.checked;}).length;
  var ind=kids.filter(function(c){return c.indeterminate;}).length;
  var pcb=pli.querySelector(':scope > .node > input');
  pcb.checked = on===kids.length && ind===0;
  pcb.indeterminate = !pcb.checked && (on>0 || ind>0);
  up(pli);
}
function picked(){return LEAVES.filter(function(li){return !li.classList.contains('hide') && li.querySelector('input').checked;});}
function count(){
  var n=picked().length;
  document.getElementById('go').textContent='선택 Export ('+n+')';
  document.getElementById('go').disabled = n===0;
}
document.getElementById('load').onclick=function(){
  var plc=document.getElementById('plc').value.trim();
  if(!plc){setStatus('PLC 이름을 입력하세요',true);return;}
  setStatus('불러오는 중...');
  fetch('/api/plc/tree?plc='+encodeURIComponent(plc)).then(function(r){
    return r.json().then(function(j){return {ok:r.ok,j:j};});
  }).then(function(x){
    if(!x.ok){setStatus('실패: '+(x.j.error||'알 수 없는 오류'),true);return;}
    LEAVES=[];
    var host=document.getElementById('tree'); host.className=''; host.innerHTML='';
    var ul=el('ul','root'); build(x.j.tree||[], ul); host.appendChild(ul);
    setStatus(x.j.plc+' - export 가능한 XML '+x.j.leafCount+'개');
    count();
  }).catch(function(e){setStatus('요청 실패: '+e.message+' (서버가 떠 있나요?)',true);});
};
document.getElementById('filter').oninput=function(){
  var q=this.value.trim().toLowerCase();
  LEAVES.forEach(function(li){li.classList.toggle('hide', q!=='' && li.dataset.name.indexOf(q)<0);});
  document.querySelectorAll('li.folder').forEach(function(f){
    var any=f.querySelectorAll('li.leaf:not(.hide)').length>0;
    f.classList.toggle('hide', q!=='' && !any);
  });
  count();
};
function setAll(v){
  document.querySelectorAll('#tree input[type=checkbox]').forEach(function(c){c.checked=v;c.indeterminate=false;});
  count();
}
document.getElementById('all').onclick=function(){setAll(true);};
document.getElementById('none').onclick=function(){setAll(false);};
document.getElementById('go').onclick=function(){
  var plc=document.getElementById('plc').value.trim();
  var paths=picked().map(function(li){return li.dataset.path;});
  if(!paths.length) return;
  var cmp=document.getElementById('compile').checked ? '&compile=true' : '';
  setStatus(paths.length+'개 export 중... (TIA 가 응답할 때까지 걸립니다)');
  document.getElementById('go').disabled=true;
  fetch('/api/plc/export/selected?plc='+encodeURIComponent(plc)+cmp,{
    method:'POST', headers:{'Content-Type':'application/json'},
    body:JSON.stringify({paths:paths})
  }).then(function(r){
    if(!r.ok) return r.text().then(function(t){throw new Error(t);});
    var ok=r.headers.get('X-Export-Success'), ng=r.headers.get('X-Export-Failed');
    return r.blob().then(function(b){return {b:b,ok:ok,ng:ng};});
  }).then(function(x){
    var u=URL.createObjectURL(x.b); var a=el('a'); a.href=u; a.download=plc+'_xml_selected.zip';
    document.body.appendChild(a); a.click(); a.remove(); URL.revokeObjectURL(u);
    setStatus('완료 - 성공 '+(x.ok||'?')+', 실패 '+(x.ng||'?')+'. ZIP 안 _export_report.json 에 상세가 있습니다.');
  }).catch(function(e){setStatus('export 실패: '+e.message,true);})
    .then(function(){count();});
};
</script>
</body>
</html>";
            resp.ContentType = "text/html; charset=utf-8";
            byte[] buf = Encoding.UTF8.GetBytes(html);
            resp.ContentLength64 = buf.Length;
            resp.OutputStream.Write(buf, 0, buf.Length);
            resp.Close();
        }

        static void ServeSwaggerUI(HttpListenerResponse resp)
        {
            string html = @"<!DOCTYPE html>
<html lang=""ko"">
<head>
<meta charset=""UTF-8"">
<title>TIA Portal Openness REST API</title>
<link rel=""stylesheet"" href=""https://unpkg.com/swagger-ui-dist@5/swagger-ui.css"" />
<style>
body { margin: 0; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; }
.topbar-wrapper img { content: url('data:image/svg+xml;utf8,<svg xmlns=""http://www.w3.org/2000/svg"" width=""40"" height=""40""><rect width=""40"" height=""40"" rx=""6"" fill=""%2349cc90""/><text x=""20"" y=""27"" text-anchor=""middle"" fill=""white"" font-size=""20"" font-weight=""bold"">T</text></svg>'); }
.swagger-ui .topbar { background: #1b1b1b; }
.notice-banner { background: linear-gradient(135deg, #fff3cd 0%, #ffeaa7 100%); border-bottom: 3px solid #f0ad4e; padding: 16px 24px; display: flex; align-items: flex-start; gap: 12px; }
.notice-banner .icon { font-size: 24px; flex-shrink: 0; margin-top: 2px; }
.notice-banner .content { flex: 1; }
.notice-banner h3 { margin: 0 0 8px 0; color: #856404; font-size: 15px; }
.notice-banner ul { margin: 6px 0 0 0; padding-left: 18px; color: #664d03; font-size: 13px; line-height: 1.8; }
.dll-panel { background: #f8f9fa; border-bottom: 2px solid #dee2e6; padding: 20px 24px; }
.dll-panel h3 { margin: 0 0 12px 0; font-size: 15px; color: #333; }
.dll-panel .status-area { margin-bottom: 12px; font-size: 13px; color: #555; }
.dll-panel .status-item { padding: 4px 0; }
.dll-panel .found { color: #28a745; }
.dll-panel .missing { color: #dc3545; }
</style>
</head>
<body>
<div class=""notice-banner"">
  <span class=""icon"">&#9888;&#65039;</span>
  <div class=""content"">
    <h3>&#47196;&#52972; &#54872;&#44221; &#51204;&#50857; API</h3>
    <ul>
      <li>Siemens TIA Portal V19 / V20 / V21&#51060; &#47196;&#52972; PC&#50640; &#49444;&#52824;&#46104;&#50612; &#51080;&#50612;&#50556; &#54633;&#45768;&#45796;</li>
      <li>Windows &#49324;&#50857;&#51088;&#44032; 'Siemens TIA Openness' &#44536;&#47353;&#50640; &#46321;&#47197;&#46104;&#50612; &#51080;&#50612;&#50556; &#54633;&#45768;&#45796;</li>
      <li>TiaApiServer.exe&#47484; &#49892;&#54665;&#54620; &#54980; /api/connect&#47484; &#54840;&#52636;&#54616;&#50668; TIA Portal&#50640; &#50672;&#44208;&#54616;&#49464;&#50836;</li>
    </ul>
    <div style=""margin-top:10px;padding-top:8px;border-top:1px solid #f0ad4e55""><a href=""/ui/device-properties"" style=""background:#0d6efd;color:white;padding:6px 14px;border-radius:3px;text-decoration:none;font-weight:600;font-size:13px"">&#9881;&#65039; CPU &#49549;&#49457; &#54200;&#51665;&#44592;</a><a href=""/ui/network-properties"" style=""margin-left:8px;background:#198754;color:white;padding:6px 14px;border-radius:3px;text-decoration:none;font-weight:600;font-size:13px"">&#127760; &#45348;&#53944;&#50892;&#53356; &#49549;&#49457; &#54200;&#51665;&#44592; (PN/DP)</a><span style=""margin-left:10px;color:#664d03;font-size:12px"">TIA Portal Properties &#52285; + &#49688;&#51221;/&#51200;&#51109; &#44592;&#45733;</span></div>
  </div>
</div>
<div class=""dll-panel"">
  <h3>Openness &#50612;&#49472;&#48660;&#47532;</h3>
  <div class=""status-area"" id=""dll-status"">&#53448;&#51648; &#51473;...</div>
</div>
<div id=""swagger-ui""></div>
<script src=""https://unpkg.com/swagger-ui-dist@5/swagger-ui-bundle.js""></script>
<script>
SwaggerUIBundle({
  url: '/api/openapi.json',
  dom_id: '#swagger-ui',
  deepLinking: true,
  presets: [SwaggerUIBundle.presets.apis, SwaggerUIBundle.SwaggerUIStandalonePreset],
  layout: 'BaseLayout',
  defaultModelsExpandDepth: -1,
  docExpansion: 'list',
  tryItOutEnabled: true
});

function checkDllStatus() {
  fetch('/api/dll/status').then(r => r.json()).then(data => {
    var html = '';
    data.dlls.forEach(function(d) {
      if (d.found) html += '<div class=""status-item found"">&#10003; ' + d.name + ' (' + d.location + ')</div>';
      else html += '<div class=""status-item missing"">&#10007; ' + d.name + ' - &#52286;&#51648; &#47803;&#54632;</div>';
    });
    if (data.ready) html = '<div class=""status-item found""><strong>&#10003; Openness &#50612;&#49472;&#48660;&#47532; &#51456;&#48708; &#50756;&#47308;</strong></div>' + html;
    document.getElementById('dll-status').innerHTML = html;
  }).catch(function() {
    document.getElementById('dll-status').innerHTML = '<div class=""status-item missing"">API &#49436;&#48260;&#50640; &#50672;&#44208;&#54624; &#49688; &#50630;&#49845;&#45768;&#45796;</div>';
  });
}

checkDllStatus();
</script>
</body>
</html>";
            resp.StatusCode = 200;
            resp.ContentType = "text/html; charset=utf-8";
            byte[] buf = Encoding.UTF8.GetBytes(html);
            resp.ContentLength64 = buf.Length;
            resp.OutputStream.Write(buf, 0, buf.Length);
            resp.Close();
        }
    }
    /// <summary>
    /// 진짜 진입점. Openness 타입이 등장하는 어떤 코드보다 먼저 리졸버를 건다.
    /// Run 은 NoInlining 이어야 한다 — 인라인되면 Program 참조가 Main 본문으로
    /// 끌려들어와 JIT 가 Install() 전에 Program 을 건드린다.
    /// </summary>
    /// <summary>
    /// 진짜 진입점. Openness 타입이 등장하는 어떤 코드보다 먼저 리졸버를 건다.
    ///
    /// HTTP 루프도 여기 있다 — Program 안에 두면 첫 요청을 처리하는 순간
    /// 그 클래스의 static 필드(TiaPortal·UserGlobalLibrary)가 초기화되면서
    /// Openness 가 로드돼, 버전 전환 엔드포인트가 영영 늦어 버린다.
    /// 그래서 버전 관련 요청은 TiaBootstrap 이 먼저 낚아채고, 나머지만
    /// NoInlining 래퍼를 거쳐 Program 으로 넘긴다.
    /// </summary>
    internal static class Entry
    {
        static void Main(string[] args)
        {
            TiaBootstrap.Install();
            TiaBootstrap.ParsePort(args);

            string prefix = "http://localhost:" + TiaBootstrap.port + "/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            TiaBootstrap.PrintBanner(prefix);

            while (true)
            {
                var ctx = listener.GetContext();
                try
                {
                    // Openness 를 건드리지 않는 경로 (버전 조회/전환)
                    if (TiaBootstrap.TryHandle(ctx)) continue;
                    Dispatch(ctx);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("ERROR: " + ex.Message);
                    try { TiaBootstrap.SendJson(ctx.Response, 500, new { error = ex.Message }); }
                    catch { }
                }
            }
        }

        /// <summary>
        /// NoInlining 필수 — 인라인되면 Program 참조가 Main 본문으로 끌려들어와
        /// JIT 가 Install() 전에 Program 을 건드린다.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Dispatch(HttpListenerContext ctx)
        {
            Program.HandleRequest(ctx);
        }
    }

}
