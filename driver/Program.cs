// DSH SolidWorks driver: one stateless COM automation run per invocation.
// Reads a JSON request, talks to the running (or freshly started) SolidWorks
// through its early-bound interop API, and writes a JSON response.
//
// Compiled by the plugin with the in-box .NET Framework csc.exe together with
// SolidWorks.Interop.sldworks.dll and SolidWorks.Interop.swconst.dll.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace DshSolidWorks
{
    sealed class SwFailure : Exception
    {
        public readonly string Kind;

        public SwFailure(string kind, string message) : base(message)
        {
            Kind = kind;
        }
    }

    static class Program
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
            RecursionLimit = 200
        };

        static ISldWorks sw;
        static Dictionary<string, object> request;

        static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: dsh-solidworks-driver <request.json> <response.json>");
                return 64;
            }

            var result = new Dictionary<string, object>();
            try
            {
                string raw = File.ReadAllText(args[0], Encoding.UTF8);
                request = Json.Deserialize<Dictionary<string, object>>(raw);
                if (request == null) throw new SwFailure("bad_request", "The request file is empty.");

                object data = Dispatch();
                result["ok"] = true;
                result["data"] = data;
            }
            catch (SwFailure failure)
            {
                result["ok"] = false;
                result["error"] = Error(failure.Kind, failure.Message, null);
            }
            catch (Exception ex)
            {
                result["ok"] = false;
                result["error"] = Error("exception", Describe(ex), ex.ToString());
            }
            finally
            {
                try
                {
                    if (sw != null) sw.CommandInProgress = false;
                }
                catch { }
            }

            try
            {
                string target = Path.GetFullPath(args[1]);
                string folder = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.WriteAllText(target, Json.Serialize(result), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("failed to write the response: " + ex.Message);
                return 70;
            }

            return 0;
        }

        static Dictionary<string, object> Error(string kind, string message, string detail)
        {
            var error = new Dictionary<string, object>
            {
                { "kind", kind },
                { "message", message }
            };
            if (!string.IsNullOrEmpty(detail)) error["detail"] = detail;
            return error;
        }

        static string Describe(Exception ex)
        {
            string message = ex.Message;
            return ex.GetType().Name + (string.IsNullOrEmpty(message) ? "" : ": " + message);
        }

        static object Dispatch()
        {
            string op = Str("op", "");
            switch (op)
            {
                case "status": return Status();
                case "open": return Open();
                case "documents": return Documents();
                case "inspect": return Inspect();
                case "properties": return Properties();
                case "export": return Export();
                case "capture": return Capture();
                case "save": return Save();
                case "model": return Build();
                case "assembly": return AssemblyBuild();
                case "drawing": return DrawingBuild();
                default: throw new SwFailure("bad_request", "Unknown operation '" + op + "'.");
            }
        }

        // ---------------------------------------------------------------- basics

        static string Str(string key)
        {
            return Str(key, null);
        }

        static string Str(string key, string fallback)
        {
            object value;
            if (request != null && request.TryGetValue(key, out value) && value != null)
            {
                string text = value as string;
                if (text != null) return text;
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            return fallback;
        }

        static bool Bool(string key, bool fallback)
        {
            object value;
            if (request != null && request.TryGetValue(key, out value) && value != null)
            {
                if (value is bool) return (bool)value;
                bool parsed;
                if (bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed)) return parsed;
            }
            return fallback;
        }

        static int Int(string key, int fallback)
        {
            object value;
            if (request != null && request.TryGetValue(key, out value) && value != null)
            {
                if (value is int) return (int)value;
                double number;
                if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out number))
                    return (int)Math.Round(number);
            }
            return fallback;
        }

        static Dictionary<string, object> Obj(string key)
        {
            object value;
            if (request != null && request.TryGetValue(key, out value))
                return value as Dictionary<string, object>;
            return null;
        }

        static bool Visible()
        {
            return Bool("visible", true);
        }

        static int WaitSeconds()
        {
            int seconds = Int("timeoutSec", 180);
            if (seconds < 10) seconds = 10;
            if (seconds > 900) seconds = 900;
            return seconds;
        }

        static string TypeName(int type)
        {
            switch (type)
            {
                case 1: return "part";
                case 2: return "assembly";
                case 3: return "drawing";
                default: return "document";
            }
        }

        static int TypeOfPath(string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".sldprt": return 1;
                case ".sldasm": return 2;
                case ".slddrw": return 3;
                default: throw new SwFailure("bad_request",
                    "Cannot tell the SolidWorks document type from '" + Path.GetFileName(path) + "'. Use a .sldprt, .sldasm or .slddrw path.");
            }
        }

        // ------------------------------------------------------ install / launch

        static string FindExe()
        {
            string configured = Str("exePath", null);
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;

            try
            {
                using (RegistryKey key = Registry.ClassesRoot.OpenSubKey(@"SldWorks.Application\CLSID"))
                {
                    if (key != null)
                    {
                        string clsid = Convert.ToString(key.GetValue(""), CultureInfo.InvariantCulture);
                        if (!string.IsNullOrEmpty(clsid))
                        {
                            using (RegistryKey server = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + clsid + @"\LocalServer32"))
                            {
                                if (server != null)
                                {
                                    string raw = Convert.ToString(server.GetValue(""), CultureInfo.InvariantCulture);
                                    if (!string.IsNullOrEmpty(raw))
                                    {
                                        raw = raw.Trim();
                                        if (raw.StartsWith("\""))
                                        {
                                            int end = raw.IndexOf('"', 1);
                                            if (end > 0) raw = raw.Substring(1, end - 1);
                                        }
                                        else
                                        {
                                            int space = raw.IndexOf(' ');
                                            if (space > 0) raw = raw.Substring(0, space);
                                        }
                                        if (File.Exists(raw)) return raw;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            string[] candidates =
            {
                @"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe",
                @"D:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe",
                @"E:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe",
                @"C:\Program Files\SolidWorks Corp\SolidWorks\SLDWORKS.exe"
            };
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        static bool IsRunning()
        {
            try
            {
                return Process.GetProcessesByName("SLDWORKS").Length > 0;
            }
            catch
            {
                return false;
            }
        }

        static ISldWorks Attach(bool launch)
        {
            if (sw != null) return sw;

            bool running = IsRunning();
            if (!running)
            {
                if (!launch)
                {
                    throw new SwFailure("not_running",
                        "SolidWorks is not running. Open it, or call the tool with launch=true (starting SolidWorks takes about 30 seconds).");
                }
                string exe = FindExe();
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                {
                    throw new SwFailure("not_installed",
                        "SolidWorks is not installed on this computer (no SLDWORKS.exe and no SldWorks.Application COM registration).");
                }
                try
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    throw new SwFailure("launch_failed", "Could not start SolidWorks: " + Describe(ex));
                }
                Thread.Sleep(2500);
            }

            int deadline = System.Environment.TickCount + WaitSeconds() * 1000;
            Exception last = null;
            while (System.Environment.TickCount < deadline)
            {
                try
                {
                    object application = Activator.CreateInstance(Type.GetTypeFromProgID("SldWorks.Application"));
                    ISldWorks candidate = (ISldWorks)application;
                    candidate.RevisionNumber();
                    try { candidate.Visible = Visible(); } catch { }
                    sw = candidate;
                    return sw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(1500);
                }
            }

            throw new SwFailure("connect_failed",
                "SolidWorks did not answer COM automation within " + WaitSeconds() + " seconds." +
                (last == null ? "" : " Last error: " + Describe(last)));
        }

        static ISldWorks App(bool launch)
        {
            if (sw != null) return sw;
            return Attach(launch);
        }

        // ------------------------------------------------------------- documents

        static ModelDoc2[] OpenDocs(ISldWorks app)
        {
            object raw = app.GetDocuments();
            object[] items = raw as object[];
            if (items == null) return new ModelDoc2[0];
            var list = new List<ModelDoc2>();
            foreach (object item in items)
            {
                ModelDoc2 model = item as ModelDoc2;
                if (model != null) list.Add(model);
            }
            return list.ToArray();
        }

        static Dictionary<string, object> DocInfo(ModelDoc2 model, ModelDoc2 active)
        {
            if (model == null) return null;
            bool isActive = active != null &&
                string.Equals(model.GetTitle(), active.GetTitle(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(model.GetPathName() ?? "", active.GetPathName() ?? "", StringComparison.OrdinalIgnoreCase);
            return new Dictionary<string, object>
            {
                { "title", model.GetTitle() },
                { "path", model.GetPathName() ?? "" },
                { "type", TypeName(model.GetType()) },
                { "active", isActive },
                { "modified", model.GetSaveFlag() },
                { "readOnly", model.IsOpenedReadOnly() }
            };
        }

        static Dictionary<string, object> DocList(ISldWorks app)
        {
            ModelDoc2 active = app.ActiveDoc as ModelDoc2;
            var list = new List<object>();
            foreach (ModelDoc2 model in OpenDocs(app))
            {
                if (model != null) list.Add(DocInfo(model, active));
            }
            return new Dictionary<string, object>
            {
                { "documents", list },
                { "active", DocInfo(active, active) },
                { "count", list.Count }
            };
        }

        static ModelDoc2 ResolveDoc(bool activate)
        {
            ISldWorks app = App(Bool("launch", true));
            string path = Str("path");
            string title = Str("title");
            ModelDoc2 found = null;

            if (!string.IsNullOrEmpty(path))
            {
                string wanted = SafePath(path);
                foreach (ModelDoc2 model in OpenDocs(app))
                {
                    if (string.Equals(SafePath(model.GetPathName()), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        found = model;
                        break;
                    }
                }
                if (found == null)
                {
                    throw new SwFailure("not_open",
                        "Successfully opened documents:\n" + DescribeOpenDocs(app) +
                        "\n'" + path + "' is not open. Use solidworks_open first.");
                }
            }
            else if (!string.IsNullOrEmpty(title))
            {
                foreach (ModelDoc2 model in OpenDocs(app))
                {
                    if (string.Equals(model.GetTitle(), title, StringComparison.OrdinalIgnoreCase))
                    {
                        found = model;
                        break;
                    }
                }
                if (found == null)
                {
                    throw new SwFailure("not_open",
                        "No open document is titled '" + title + "'. Open documents:\n" + DescribeOpenDocs(app));
                }
            }
            else
            {
                found = app.ActiveDoc as ModelDoc2;
                if (found == null)
                {
                    throw new SwFailure("no_document",
                        "SolidWorks has no active document. Use solidworks_open to open a file or create a new one.");
                }
            }

            if (activate)
            {
                int errors = 0;
                try { app.ActivateDoc3(found.GetTitle(), false, 0, ref errors); } catch { }
            }
            return found;
        }

        static string DescribeOpenDocs(ISldWorks app)
        {
            var lines = new List<string>();
            foreach (ModelDoc2 model in OpenDocs(app))
            {
                lines.Add("  - " + model.GetTitle() + "  [" + TypeName(model.GetType()) + "]  " +
                          (string.IsNullOrEmpty(model.GetPathName()) ? "(not saved yet)" : model.GetPathName()));
            }
            if (lines.Count == 0) lines.Add("  (none)");
            return string.Join("\n", lines.ToArray());
        }

        static string SafePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        // ---------------------------------------------------------------- status

        static object Status()
        {
            var data = new Dictionary<string, object>();
            string exe = FindExe();
            bool installed = !string.IsNullOrEmpty(exe) && File.Exists(exe);
            data["installed"] = installed;
            data["exePath"] = exe ?? "";
            data["exeVersion"] = "";
            if (installed)
            {
                try { data["exeVersion"] = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? ""; } catch { }
            }

            bool running = IsRunning();
            data["running"] = running;
            data["attached"] = false;
            data["visible"] = false;
            data["version"] = "";
            data["documents"] = new List<object>();
            data["count"] = 0;
            data["active"] = null;
            data["templates"] = new Dictionary<string, object>();

            bool attach = Bool("attach", running);
            if (attach && (running || Bool("launch", false)))
            {
                ISldWorks app = Attach(false);
                data["attached"] = true;
                data["version"] = app.RevisionNumber();
                try { data["visible"] = app.Visible; } catch { }
                data["exePath"] = exe ?? "";
                try
                {
                    var docs = DocList(app);
                    foreach (string key in docs.Keys) data[key] = docs[key];
                }
                catch { }
                var templates = new Dictionary<string, object>
                {
                    { "part", app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart) },
                    { "assembly", app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly) },
                    { "drawing", app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateDrawing) }
                };
                data["templates"] = templates;
            }
            return data;
        }

        // ------------------------------------------------------------------ open

        static object Open()
        {
            ISldWorks app = App(true);
            string path = Str("path");
            string kind = Str("kind");
            string saveAs = Str("saveAs");
            bool readOnly = Bool("readOnly", false);
            bool activate = Bool("activate", true);
            int errors = 0;
            int warnings = 0;
            ModelDoc2 model = null;
            bool created = false;

            if (string.IsNullOrEmpty(path) || !string.IsNullOrEmpty(kind))
            {
                string template;
                int type;
                if (!string.IsNullOrEmpty(kind))
                {
                    switch (kind.ToLowerInvariant())
                    {
                        case "part": template = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart); type = 1; break;
                        case "assembly": template = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly); type = 2; break;
                        case "drawing": template = app.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateDrawing); type = 3; break;
                        default: throw new SwFailure("bad_request", "kind must be part, assembly or drawing.");
                    }
                    if (string.IsNullOrEmpty(template) || !File.Exists(template))
                    {
                        throw new SwFailure("no_template",
                            "SolidWorks has no default " + kind + " template configured. Set one in Tools > Options > System Options > Default Templates.");
                    }
                }
                else
                {
                    type = TypeOfPath(path);
                    template = null;
                }

                if (template != null)
                {
                    model = app.NewDocument(template, 0, 0, 0) as ModelDoc2;
                    created = true;
                    if (model == null)
                        throw new SwFailure("new_failed", "SolidWorks did not create a new " + kind + " document.");
                    if (!string.IsNullOrEmpty(saveAs))
                    {
                        // The document type comes from kind; saveAs only names the file.
                        int e2 = 0, w2 = 0;
                        model.Extension.SaveAs(saveAs, 0, 1, null, ref e2, ref w2);
                        if (!File.Exists(saveAs))
                            throw new SwFailure("save_failed", "Could not save the new document to '" + saveAs + "' (error " + e2 + ").");
                        path = saveAs;
                    }
                }
            }

            if (model == null && !string.IsNullOrEmpty(path))
            {
                if (!File.Exists(path))
                    throw new SwFailure("not_found", "The file does not exist: " + path);
                int type = TypeOfPath(path);
                int options = (int)swOpenDocOptions_e.swOpenDocOptions_Silent;
                if (readOnly) options |= (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly;
                model = app.OpenDoc6(path, type, options, "", ref errors, ref warnings);
                if (model == null)
                {
                    throw new SwFailure("open_failed",
                        "SolidWorks could not open '" + path + "' (error " + errors + ", warning " + warnings + "). " +
                        "A file that was created by a newer SolidWorks version, is locked, or has missing references cannot be opened here.");
                }
            }

            if (model == null) throw new SwFailure("bad_request", "Pass a path to open, or kind to create a new document.");

            if (activate)
            {
                int activateErrors = 0;
                try { app.ActivateDoc3(model.GetTitle(), false, 0, ref activateErrors); } catch { }
            }

            return new Dictionary<string, object>
            {
                { "created", created },
                { "doc", DocInfo(model, app.ActiveDoc as ModelDoc2) }
            };
        }

        // ------------------------------------------------------------- documents

        static object Documents()
        {
            ISldWorks app = App(true);
            return DocList(app);
        }

        // --------------------------------------------------------------- inspect

        static object Inspect()
        {
            ModelDoc2 model = ResolveDoc(true);
            int type = model.GetType();
            var data = new Dictionary<string, object>();
            data["doc"] = DocInfo(model, App(false).ActiveDoc as ModelDoc2);

            var configurations = new List<object>();
            try
            {
                object namesRaw = model.GetConfigurationNames();
                string[] names = namesRaw as string[];
                if (names != null)
                {
                    string activeName = null;
                    try { activeName = model.ConfigurationManager.ActiveConfiguration.Name; } catch { }
                    foreach (string name in names)
                    {
                        configurations.Add(new Dictionary<string, object>
                        {
                            { "name", name },
                            { "active", string.Equals(name, activeName, StringComparison.OrdinalIgnoreCase) }
                        });
                    }
                    data["activeConfiguration"] = activeName ?? "";
                }
            }
            catch { }
            data["configurations"] = configurations;

            data["customProperties"] = ReadProperties(model, "");

            string material = "";
            try
            {
                PartDoc part = model as PartDoc;
                if (part != null)
                {
                    string database = "";
                    material = part.GetMaterialPropertyName2("", out database) ?? "";
                    if (!string.IsNullOrEmpty(database) && !string.IsNullOrEmpty(material))
                        material = material + "  (" + database + ")";
                }
            }
            catch { }
            data["material"] = material;

            data["mass"] = ReadMass(model);

            var box = ReadBox(model, type);
            data["boundingBox"] = box;

            var features = new List<object>();
            try
            {
                Feature feature = model.FirstFeature() as Feature;
                int guard = 0;
                while (feature != null && guard < 400)
                {
                    guard++;
                    string featureType = "";
                    string name = "";
                    try { featureType = feature.GetTypeName2() ?? ""; } catch { }
                    try { name = feature.Name ?? ""; } catch { }
                    bool isFolder = featureType.EndsWith("Folder", StringComparison.OrdinalIgnoreCase) ||
                                    featureType == "DetailCabinet" || featureType == "OriginProfileFeature";
                    if (!isFolder && !string.IsNullOrEmpty(name))
                    {
                        features.Add(new Dictionary<string, object> { { "name", name }, { "type", featureType } });
                    }
                    try { feature = feature.GetNextFeature() as Feature; } catch { feature = null; }
                }
            }
            catch { }
            data["features"] = features;

            if (type == 2)
            {
                var components = new List<object>();
                try
                {
                    AssemblyDoc assembly = model as AssemblyDoc;
                    if (assembly != null)
                    {
                        object raw = assembly.GetComponents(true);
                        object[] items = raw as object[];
                        int count = 0;
                        if (items != null)
                        {
                            foreach (object item in items)
                            {
                                if (count >= 120) break;
                                Component2 component = item as Component2;
                                if (component == null) continue;
                                count++;
                                var entry = new Dictionary<string, object>
                                {
                                    { "name", component.Name2 ?? "" },
                                    { "path", component.GetPathName() ?? "" },
                                    { "configuration", component.ReferencedConfiguration ?? "" }
                                };
                                try { entry["suppressed"] = component.IsSuppressed(); } catch { }
                                components.Add(entry);
                            }
                        }
                        data["components"] = components;
                        data["componentCount"] = count;
                    }
                }
                catch { }
            }

            if (type == 3)
            {
                var sheets = new List<object>();
                try
                {
                    DrawingDoc drawing = model as DrawingDoc;
                    if (drawing != null)
                    {
                        object raw = drawing.GetSheetNames();
                        string[] names = raw as string[];
                        if (names != null) foreach (string name in names) sheets.Add(name);
                        data["sheets"] = sheets;
                        data["viewCount"] = drawing.GetViewCount();
                        object current = drawing.GetCurrentSheet();
                        Sheet sheet = current as Sheet;
                        if (sheet != null) data["currentSheet"] = sheet.GetName() ?? "";
                    }
                }
                catch { }
            }

            data["bodyCount"] = ReadBodyCount(model, type);
            data["sheetBodyCount"] = ReadSheetBodyCount(model, type);

            if (type == 1 && Bool("edges", false))
            {
                var edgeList = new List<object>();
                int edgeIndex = 0;
                foreach (Edge edge in CollectEdges(model))
                {
                    edgeIndex++;
                    var entry = new Dictionary<string, object> { { "index", edgeIndex } };
                    try
                    {
                        Vertex first = edge.GetStartVertex() as Vertex;
                        Vertex second = edge.GetEndVertex() as Vertex;
                        double[] p1 = first != null ? first.GetPoint() as double[] : null;
                        double[] p2 = second != null ? second.GetPoint() as double[] : null;
                        if (p1 != null && p1.Length >= 3)
                            entry["start"] = new object[] { Num(p1[0] * 1000), Num(p1[1] * 1000), Num(p1[2] * 1000) };
                        if (p2 != null && p2.Length >= 3)
                            entry["end"] = new object[] { Num(p2[0] * 1000), Num(p2[1] * 1000), Num(p2[2] * 1000) };
                        if (p1 != null && p2 != null && p1.Length >= 3 && p2.Length >= 3)
                        {
                            double dx = p2[0] - p1[0];
                            double dy = p2[1] - p1[1];
                            double dz = p2[2] - p1[2];
                            entry["chord"] = Num(Math.Sqrt(dx * dx + dy * dy + dz * dz) * 1000);
                        }
                    }
                    catch { }
                    edgeList.Add(entry);
                }
                data["edges"] = edgeList;
            }

            return data;
        }

        static Dictionary<string, object> ReadMass(ModelDoc2 model)
        {
            try
            {
                MassProperty property = model.Extension.CreateMassProperty();
                if (property == null) return null;
                property.UseSystemUnits = true;
                var mass = new Dictionary<string, object>();
                mass["mass"] = Num(property.Mass);
                mass["density"] = Num(property.Density);
                mass["volume"] = Num(property.Volume);
                mass["area"] = Num(property.SurfaceArea);
                double[] center = property.CenterOfMass as double[];
                if (center != null && center.Length >= 3)
                    mass["centerOfMass"] = new object[] { Num(center[0]), Num(center[1]), Num(center[2]) };
                return mass;
            }
            catch
            {
                return null;
            }
        }

        static Dictionary<string, object> ReadBox(ModelDoc2 model, int type)
        {
            try
            {
                if (type == 1)
                {
                    PartDoc part = model as PartDoc;
                    if (part == null) return null;
                    double[] box = part.GetPartBox(true) as double[];
                    if (box == null || box.Length < 6) return null;
                    return new Dictionary<string, object>
                    {
                        { "min", new object[] { Num(box[0]), Num(box[1]), Num(box[2]) } },
                        { "max", new object[] { Num(box[3]), Num(box[4]), Num(box[5]) } },
                        { "size", new object[] { Num(box[3] - box[0]), Num(box[4] - box[1]), Num(box[5] - box[2]) } }
                    };
                }
            }
            catch { }
            return null;
        }

        static int ReadSheetBodyCount(ModelDoc2 model, int type)
        {
            try
            {
                if (type == 1)
                {
                    PartDoc part = model as PartDoc;
                    if (part == null) return 0;
                    object[] bodies = part.GetBodies2((int)swBodyType_e.swSheetBody, false) as object[];
                    return bodies == null ? 0 : bodies.Length;
                }
            }
            catch { }
            return 0;
        }

        static int ReadBodyCount(ModelDoc2 model, int type)
        {
            try
            {
                if (type == 1)
                {
                    PartDoc part = model as PartDoc;
                    if (part == null) return 0;
                    object raw = part.GetBodies2((int)swBodyType_e.swSolidBody, true);
                    object[] bodies = raw as object[];
                    return bodies == null ? 0 : bodies.Length;
                }
            }
            catch { }
            return 0;
        }

        // ------------------------------------------------------------ properties

        static object Properties()
        {
            ModelDoc2 model = ResolveDoc(true);
            string action = Str("action", "get").ToLowerInvariant();
            string configuration = Str("configuration", "");

            CustomPropertyManager manager = model.Extension.get_CustomPropertyManager(configuration ?? "");
            if (manager == null)
                throw new SwFailure("unsupported", "This document has no custom property manager.");

            int changed = 0;
            if (action == "set")
            {
                Dictionary<string, object> values = Obj("properties");
                if (values == null || values.Count == 0)
                    throw new SwFailure("bad_request", "Pass a properties object such as {\"PartNumber\": \"A-001\"}.");
                foreach (KeyValuePair<string, object> pair in values)
                {
                    if (string.IsNullOrEmpty(pair.Key)) continue;
                    string value = Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? "";
                    int result = manager.Add3(pair.Key, (int)swCustomInfoType_e.swCustomInfoText, value, 1);
                    if (result == 0) changed++;
                }
            }
            else if (action == "delete")
            {
                Dictionary<string, object> values = Obj("properties");
                List<string> names = new List<string>();
                if (values != null) foreach (string key in values.Keys) names.Add(key);
                else
                {
                    string single = Str("name");
                    if (!string.IsNullOrEmpty(single)) names.Add(single);
                }
                if (names.Count == 0) throw new SwFailure("bad_request", "Pass the property names to delete.");
                foreach (string name in names)
                {
                    manager.Delete2(name);
                    changed++;
                }
            }

            return new Dictionary<string, object>
            {
                { "doc", DocInfo(model, App(false).ActiveDoc as ModelDoc2) },
                { "configuration", configuration ?? "" },
                { "properties", ReadProperties(model, configuration) },
                { "changed", changed }
            };
        }

        static Dictionary<string, object> ReadProperties(ModelDoc2 model, string configuration)
        {
            var result = new Dictionary<string, object>();
            try
            {
                CustomPropertyManager manager = model.Extension.get_CustomPropertyManager(configuration ?? "");
                if (manager == null) return result;
                object raw = manager.GetNames();
                object[] names = raw as object[];
                if (names == null) return result;
                foreach (object item in names)
                {
                    string name = Convert.ToString(item, CultureInfo.InvariantCulture);
                    if (string.IsNullOrEmpty(name)) continue;
                    string value = "";
                    string resolved = "";
                    bool wasResolved = false;
                    bool linked = false;
                    int status = manager.Get6(name, false, out value, out resolved, out wasResolved, out linked);
                    result[name] = value ?? "";
                }
            }
            catch { }
            return result;
        }

        // ---------------------------------------------------------------- export

        static readonly string[] RasterFormats = { "bmp", "png", "jpg", "jpeg", "gif", "tif", "tiff" };

        static bool IsRaster(string format)
        {
            foreach (string item in RasterFormats)
                if (item == format) return true;
            return false;
        }

        static object Export()
        {
            ModelDoc2 model = ResolveDoc(true);
            string outPath = Str("out");
            string format = (Str("format") ?? "").Trim().TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(format) && !string.IsNullOrEmpty(outPath))
                format = Path.GetExtension(outPath).TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(format))
                throw new SwFailure("bad_request", "Pass format (pdf, step, iges, stl, dxf, png, ...) or an out path with the extension.");

            if (IsRaster(format))
            {
                if (string.IsNullOrEmpty(outPath)) outPath = DefaultOutPath(model, format);
                outPath = Path.ChangeExtension(outPath, format);
                int width = Int("width", 1600);
                int height = Int("height", 1200);
                CaptureRaster(model, outPath, format, width, height, Bool("zoomToFit", true));
                return new Dictionary<string, object>
                {
                    { "doc", DocInfo(model, App(false).ActiveDoc as ModelDoc2) },
                    { "out", outPath },
                    { "format", format },
                    { "bytes", File.Exists(outPath) ? new FileInfo(outPath).Length : 0 }
                };
            }

            if (string.IsNullOrEmpty(outPath)) outPath = DefaultOutPath(model, format);
            if (string.IsNullOrEmpty(Path.GetExtension(outPath))) outPath = outPath + "." + format;
            string folder = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            int errors = 0;
            int warnings = 0;
            bool saved = model.Extension.SaveAs(outPath, 0, 1, null, ref errors, ref warnings);
            if (!File.Exists(outPath))
            {
                throw new SwFailure("export_failed",
                    "SolidWorks could not export '" + Path.GetFileName(model.GetTitle()) + "' as " + format.ToUpperInvariant() +
                    " (error " + errors + ", warning " + warnings + "). Use a format this document supports, such as pdf, step or iges for parts and assemblies, or dxf/dwg/pdf for drawings.");
            }

            return new Dictionary<string, object>
            {
                { "doc", DocInfo(model, App(false).ActiveDoc as ModelDoc2) },
                { "out", outPath },
                { "format", format },
                { "bytes", new FileInfo(outPath).Length }
            };
        }

        static string DefaultOutPath(ModelDoc2 model, string format)
        {
            string source = model.GetPathName();
            string extension = "." + format;
            if (!string.IsNullOrEmpty(source))
            {
                return Path.ChangeExtension(source, extension);
            }
            string folder = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "dsh-solidworks", "exports");
            Directory.CreateDirectory(folder);
            string name = (model.GetTitle() ?? "solidworks").Replace("/", "-").Replace("\\", "-").Replace(":", "-");
            return Path.Combine(folder, name + extension);
        }

        static object Capture()
        {
            ModelDoc2 model = ResolveDoc(true);
            int width = Int("width", 1400);
            int height = Int("height", 900);
            string folder = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "dsh-solidworks", "captures");
            Directory.CreateDirectory(folder);
            string name = (model.GetTitle() ?? "solidworks").Replace("/", "-").Replace("\\", "-").Replace(":", "-");
            string outPath = Path.Combine(folder, name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".png");

            CaptureRaster(model, outPath, "png", width, height, Bool("zoomToFit", true));
            return new Dictionary<string, object>
            {
                { "doc", DocInfo(model, App(false).ActiveDoc as ModelDoc2) },
                { "png", outPath },
                { "width", width },
                { "height", height }
            };
        }

        static void CaptureRaster(ModelDoc2 model, string outPath, string format, int width, int height, bool zoomToFit)
        {
            if (width < 320) width = 320;
            if (width > 3840) width = 3840;
            if (height < 240) height = 240;
            if (height > 2160) height = 2160;

            if (zoomToFit)
            {
                try { model.ViewZoomtofit2(); } catch { }
            }

            string bmpPath = Path.Combine(Path.GetTempPath(), "dsh-sw-" + Guid.NewGuid().ToString("N") + ".bmp");
            bool ok = false;
            try { ok = model.SaveBMP(bmpPath, width, height); } catch { ok = false; }
            if (!ok || !File.Exists(bmpPath))
                throw new SwFailure("capture_failed",
                    "SolidWorks could not save the graphics area. Open a document window and try again.");

            try
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(outPath));
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                using (Image image = Image.FromFile(bmpPath))
                {
                    ImageFormat imageFormat = ImageFormat.Png;
                    switch (format)
                    {
                        case "bmp": imageFormat = ImageFormat.Bmp; break;
                        case "jpg":
                        case "jpeg": imageFormat = ImageFormat.Jpeg; break;
                        case "gif": imageFormat = ImageFormat.Gif; break;
                        case "tif":
                        case "tiff": imageFormat = ImageFormat.Tiff; break;
                    }
                    image.Save(outPath, imageFormat);
                }
            }
            finally
            {
                try { File.Delete(bmpPath); } catch { }
            }
        }

        // ------------------------------------------------------------------ save

        static object Save()
        {
            ISldWorks app = App(true);
            string action = Str("action", "save").ToLowerInvariant();
            int errors = 0;
            int warnings = 0;

            if (action == "close_all")
            {
                var closed = new List<object>();
                foreach (ModelDoc2 model in OpenDocs(app))
                {
                    string title = model.GetTitle();
                    bool dirty = model.GetSaveFlag();
                    if (dirty)
                    {
                        if (!Bool("force", false))
                        {
                            throw new SwFailure("unsaved_changes",
                                "'" + title + "' has unsaved changes. Save it first, or pass force=true to discard them.");
                        }
                        try { model.SetSaveFlag(); } catch { }
                    }
                    app.CloseDoc(title);
                    closed.Add(title);
                }
                return new Dictionary<string, object> { { "action", "close_all" }, { "closed", closed } };
            }

            ModelDoc2 doc = ResolveDoc(true);
            string title2 = doc.GetTitle();

            switch (action)
            {
                case "save":
                {
                    // Save3 also runs when SolidWorks does not consider the document dirty:
                    // custom properties written through the API do not always raise that flag.
                    bool saved = doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings);
                    if (!saved)
                        throw new SwFailure("save_failed", "SolidWorks could not save '" + title2 + "' (error " + errors + ", warning " + warnings + ").");
                    return new Dictionary<string, object> { { "action", "save" }, { "doc", DocInfo(doc, app.ActiveDoc as ModelDoc2) }, { "saved", true }, { "path", doc.GetPathName() ?? "" } };
                }
                case "save_as":
                {
                    string target = Str("out");
                    if (string.IsNullOrEmpty(target)) throw new SwFailure("bad_request", "Pass out with the file name to save as.");
                    string folder = Path.GetDirectoryName(Path.GetFullPath(target));
                    if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                    bool saved = doc.Extension.SaveAs(target, 0, 1, null, ref errors, ref warnings);
                    if (!File.Exists(target))
                        throw new SwFailure("save_failed", "Could not save '" + title2 + "' to '" + target + "' (error " + errors + ").");
                    return new Dictionary<string, object> { { "action", "save_as" }, { "doc", DocInfo(doc, app.ActiveDoc as ModelDoc2) }, { "saved", saved }, { "path", target } };
                }
                case "close":
                {
                    if (doc.GetSaveFlag() && !Bool("force", false))
                        throw new SwFailure("unsaved_changes", "'" + title2 + "' has unsaved changes. Save it first, or pass force=true to discard them.");
                    if (doc.GetSaveFlag()) { try { doc.SetSaveFlag(); } catch { } }
                    app.CloseDoc(title2);
                    return new Dictionary<string, object> { { "action", "close" }, { "title", title2 } };
                }
                default:
                    throw new SwFailure("bad_request", "action must be save, save_as, close or close_all.");
            }
        }

        // ----------------------------------------------------------------- model

        static object Build()
        {
            ModelDoc2 doc = ResolveDoc(true);
            if (doc.GetType() != 1)
                throw new SwFailure("unsupported", "Modeling works on part documents only. Open or create a part first.");

            var data = new Dictionary<string, object>();
            var notes = new List<string>();
            string backup = GuardForEdit(doc, notes, "Modeling");

            var steps = request.ContainsKey("steps") ? AsArray(request["steps"]) : null;
            if (steps == null || steps.Length == 0)
                throw new SwFailure("bad_request", "Pass a steps array with at least one modeling step.");

            var results = new List<object>();
            string lastSketch = null;
            int index = 0;

            foreach (object rawStep in steps)
            {
                index++;
                Dictionary<string, object> step = rawStep as Dictionary<string, object>;
                if (step == null) throw new SwFailure("bad_request", "Step " + index + " is not an object.");
                string op = StepStr(step, "op", "sketch").ToLowerInvariant();
                Feature created = null;
                string featureName = null;
                try
                {
                    switch (op)
                    {
                        case "sketch":
                            featureName = DrawSketch(doc, step, ref lastSketch);
                            break;
                        case "extrude":
                            created = Extrude(doc, step, ref lastSketch);
                            featureName = created.Name;
                            break;
                        case "cut":
                            created = Cut(doc, step, ref lastSketch);
                            featureName = created.Name;
                            break;
                        case "revolve":
                            created = Revolve(doc, step, ref lastSketch);
                            featureName = created.Name;
                            break;
                        case "loft":
                            created = Loft(doc, step);
                            featureName = created.Name;
                            break;
                        case "sweep":
                            created = Sweep(doc, step, ref lastSketch);
                            featureName = created.Name;
                            break;
                        case "shell":
                            created = Shell(doc, step);
                            featureName = created.Name;
                            break;
                        case "surface":
                            created = SurfaceExtrude(doc, step, ref lastSketch);
                            featureName = created.Name;
                            break;
                        case "fillet":
                            created = Fillet(doc, step);
                            featureName = created.Name;
                            break;
                        case "chamfer":
                            created = Chamfer(doc, step);
                            featureName = created.Name;
                            break;
                        default:
                            throw new SwFailure("bad_request",
                                "Unknown modeling step '" + op + "'. Use sketch, extrude, cut, revolve, loft, sweep, shell, surface, fillet or chamfer.");
                    }
                }
                catch (SwFailure failure)
                {
                    throw new SwFailure(failure.Kind, "Step " + index + " (" + op + "): " + failure.Message);
                }
                results.Add(new Dictionary<string, object>
                {
                    { "step", index },
                    { "op", op },
                    { "feature", featureName ?? "" }
                });
            }

            doc.ForceRebuild3(false);
            doc.ClearSelection2(true);
            try { doc.BlankRefGeom(); } catch { }
            try { doc.BlankSketch(); } catch { }
            try { doc.ShowNamedView2("*Isometric", (int)swStandardViews_e.swIsometricView); } catch { }
            try { doc.ViewZoomtofit2(); } catch { }

            data["doc"] = DocInfo(doc, App(false).ActiveDoc as ModelDoc2);
            data["steps"] = results;
            data["backup"] = backup;
            data["notes"] = notes;
            data["features"] = FeatureNames(doc);
            data["bodyCount"] = ReadBodyCount(doc, 1);
            data["sheetBodyCount"] = ReadSheetBodyCount(doc, 1);
            data["mass"] = ReadMass(doc);

            if (Bool("preview", true))
            {
                try
                {
                    string folder = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "dsh-solidworks", "captures");
                    Directory.CreateDirectory(folder);
                    string name = (doc.GetTitle() ?? "solidworks").Replace("/", "-").Replace("\\", "-").Replace(":", "-");
                    string outPath = Path.Combine(folder, name + "-model-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".png");
                    CaptureClean(doc, outPath, 1400, 900);
                    data["previewPng"] = outPath;
                }
                catch (Exception ex)
                {
                    notes.Add("Preview could not be captured: " + Describe(ex));
                }
            }

            return data;
        }

        /// <summary>Throws unless the document may be edited, and copies a saved file to the backup folder first.</summary>
        static string GuardForEdit(ModelDoc2 doc, List<string> notes, string action)
        {
            if (doc.IsOpenedReadOnly())
                throw new SwFailure("read_only", "This document is open read-only, so it cannot be changed.");

            string path = doc.GetPathName();
            bool saved = !string.IsNullOrEmpty(path) && File.Exists(path);
            if (saved && !Bool("allowEdit", false))
            {
                throw new SwFailure("edit_not_allowed",
                    "'" + doc.GetTitle() + "' is a saved file (" + path + "). " + action + " would change it. " +
                    "Call the tool with allowEdit=true to continue; the file is copied to a backup first.");
            }
            if (saved && Bool("backup", true))
            {
                string backup = BackupFile(path);
                notes.Add("Backup of the original file: " + backup);
                return backup;
            }
            return null;
        }

        /** Captures the graphics area with reference planes and sketches hidden, then restores the view settings. */
        static void CaptureClean(ModelDoc2 doc, string outPath, int width, int height)
        {
            bool planes = true;
            bool sketches = true;
            try { planes = doc.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDisplayPlanes); } catch { }
            try { sketches = doc.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDisplaySketches); } catch { }
            try
            {
                doc.ClearSelection2(true);
                doc.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDisplayPlanes, false);
                doc.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDisplaySketches, false);
                CaptureRaster(doc, outPath, "png", width, height, true);
            }
            finally
            {
                try { doc.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDisplayPlanes, planes); } catch { }
                try { doc.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDisplaySketches, sketches); } catch { }
            }
        }

        static string BackupFile(string path)
        {
            string folder = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "dsh-solidworks", "backups");
            Directory.CreateDirectory(folder);
            string name = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string target = Path.Combine(folder, name + "-" + stamp + extension);
            int counter = 1;
            while (File.Exists(target))
            {
                target = Path.Combine(folder, name + "-" + stamp + "-" + counter + extension);
                counter++;
            }
            File.Copy(path, target, false);
            return target;
        }

        static string DrawSketch(ModelDoc2 doc, Dictionary<string, object> step, ref string lastSketch)
        {
            string planeName = ResolvePlane(doc, StepStr(step, "plane", "front"));
            double offset = StepDouble(step, "offset", 0) / 1000.0;
            if (Math.Abs(offset) > 1e-9)
            {
                doc.ClearSelection2(true);
                if (!doc.Extension.SelectByID2(planeName, "PLANE", 0, 0, 0, false, 0, null, 0))
                    throw new SwFailure("no_plane", "Could not select the plane '" + planeName + "'.");
                int constraint = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Distance;
                double distance = offset;
                if (offset < 0)
                {
                    constraint |= (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_OptionFlip;
                    distance = -offset;
                }
                Feature planeFeature = doc.FeatureManager.InsertRefPlane(constraint, distance, 0, 0, 0, 0) as Feature;
                if (planeFeature == null)
                    throw new SwFailure("plane_failed",
                        "SolidWorks could not create a plane " + StepDouble(step, "offset", 0) + " mm from '" + planeName + "'.");
                planeName = planeFeature.Name;
            }

            doc.ClearSelection2(true);
            if (!doc.Extension.SelectByID2(planeName, "PLANE", 0, 0, 0, false, 0, null, 0))
                throw new SwFailure("no_plane", "Could not select the plane '" + planeName + "'.");

            doc.SketchManager.InsertSketch(true);
            try
            {
                object[] shapes = step.ContainsKey("shapes") ? AsArray(step["shapes"]) : null;
                if (shapes == null || shapes.Length == 0)
                    throw new SwFailure("bad_request", "A sketch step needs a shapes array with at least one shape.");
                foreach (object rawShape in shapes)
                {
                    Dictionary<string, object> shape = rawShape as Dictionary<string, object>;
                    if (shape == null) throw new SwFailure("bad_request", "Each shape must be an object.");
                    DrawShape(doc, shape);
                }
            }
            finally
            {
                doc.SketchManager.InsertSketch(true);
            }

            Feature sketch = LastFeature(doc, "ProfileFeature");
            if (sketch == null) throw new SwFailure("sketch_failed", "SolidWorks did not create the sketch.");
            lastSketch = sketch.Name;
            return sketch.Name;
        }

        static void DrawShape(ModelDoc2 doc, Dictionary<string, object> shape)
        {
            string type = StepStr(shape, "type", "").ToLowerInvariant();
            SketchManager sm = doc.SketchManager;
            double[] center = StepVector(shape, "center");
            double cx = center[0] / 1000.0;
            double cy = center[1] / 1000.0;

            switch (type)
            {
                case "rectangle":
                {
                    double width = StepDouble(shape, "width", 0) / 1000.0;
                    double height = StepDouble(shape, "height", 0) / 1000.0;
                    if (width <= 0 || height <= 0)
                        throw new SwFailure("bad_request", "A rectangle needs a positive width and height in mm.");
                    sm.CreateCornerRectangle(cx - width / 2, cy - height / 2, 0, cx + width / 2, cy + height / 2, 0);
                    break;
                }
                case "circle":
                {
                    double radius = StepDouble(shape, "radius", 0) / 1000.0;
                    if (radius <= 0) throw new SwFailure("bad_request", "A circle needs a positive radius in mm.");
                    sm.CreateCircleByRadius(cx, cy, 0, radius);
                    break;
                }
                case "polygon":
                {
                    double radius = StepDouble(shape, "radius", 0) / 1000.0;
                    int sides = (int)StepDouble(shape, "sides", 6);
                    double rotation = StepDouble(shape, "rotation", 0) * Math.PI / 180.0;
                    if (radius <= 0) throw new SwFailure("bad_request", "A polygon needs a positive radius in mm.");
                    if (sides < 3 || sides > 64) throw new SwFailure("bad_request", "A polygon needs 3 to 64 sides.");
                    double px = cx + radius * Math.Cos(rotation);
                    double py = cy + radius * Math.Sin(rotation);
                    sm.CreatePolygon(cx, cy, 0, px, py, 0, sides, true);
                    break;
                }
                case "polyline":
                {
                    object[] rawPoints = shape.ContainsKey("points") ? AsArray(shape["points"]) : null;
                    if (rawPoints == null || rawPoints.Length < 2)
                        throw new SwFailure("bad_request", "A polyline needs a points array with at least two [x, y] pairs.");
                    var points = new List<double[]>();
                    foreach (object raw in rawPoints)
                    {
                        object[] pair = AsArray(raw);
                        if (pair == null || pair.Length < 2)
                            throw new SwFailure("bad_request", "Each polyline point must be [x, y] in mm.");
                        points.Add(new double[] { AsDouble(pair[0]) / 1000.0, AsDouble(pair[1]) / 1000.0 });
                    }
                    for (int i = 0; i < points.Count - 1; i++)
                        sm.CreateLine(points[i][0], points[i][1], 0, points[i + 1][0], points[i + 1][1], 0);
                    if (StepBool(shape, "closed", false))
                    {
                        double[] first = points[0];
                        double[] last = points[points.Count - 1];
                        if (Math.Abs(first[0] - last[0]) > 1e-9 || Math.Abs(first[1] - last[1]) > 1e-9)
                            sm.CreateLine(last[0], last[1], 0, first[0], first[1], 0);
                    }
                    break;
                }
                case "centerline":
                {
                    object[] raw = shape.ContainsKey("points") ? AsArray(shape["points"]) : null;
                    if (raw == null || raw.Length < 2)
                        throw new SwFailure("bad_request", "A centerline needs a points array with two [x, y] pairs in mm.");
                    object[] first = AsArray(raw[0]);
                    object[] second = AsArray(raw[1]);
                    if (first == null || second == null || first.Length < 2 || second.Length < 2)
                        throw new SwFailure("bad_request", "Each centerline point must be [x, y] in mm.");
                    sm.CreateCenterLine(AsDouble(first[0]) / 1000.0, AsDouble(first[1]) / 1000.0, 0,
                        AsDouble(second[0]) / 1000.0, AsDouble(second[1]) / 1000.0, 0);
                    break;
                }
                default:
                    throw new SwFailure("bad_request", "Unknown shape type '" + type + "'. Use rectangle, circle, polygon, polyline or centerline.");
            }
        }

        static Feature Extrude(ModelDoc2 doc, Dictionary<string, object> step, ref string lastSketch)
        {
            string sketch = StepStr(step, "sketch", lastSketch);
            if (string.IsNullOrEmpty(sketch))
                throw new SwFailure("bad_request", "No sketch to extrude: add a sketch step first, or pass sketch with its name.");
            double depth = StepDouble(step, "depth", 10) / 1000.0;
            if (depth <= 0) throw new SwFailure("bad_request", "Extrude depth must be positive (mm).");
            bool flip = StepBool(step, "flip", false);
            string direction = StepStr(step, "direction", "blind").ToLowerInvariant();
            int endCondition = 0;
            if (direction == "midplane" || direction == "mid") endCondition = (int)swEndConditions_e.swEndCondMidPlane;
            else if (direction == "through_all" || direction == "throughall") endCondition = (int)swEndConditions_e.swEndCondThroughAll;
            else if (direction != "blind") throw new SwFailure("bad_request", "direction must be blind or midplane.");

            bool flipGiven = step.ContainsKey("flip");
            double offset = StepDouble(step, "offset", 0) / 1000.0;
            int startCondition = Math.Abs(offset) > 1e-9
                ? (int)swStartConditions_e.swStartOffset
                : (int)swStartConditions_e.swStartSketchPlane;
            double startOffset = Math.Abs(offset);
            bool flipStart = offset < 0;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                SelectSketch(doc, sketch);
                Feature feature = doc.FeatureManager.FeatureExtrusion3(true, flip, false, endCondition, 0, depth, 0.0,
                    false, false, false, false, 0, 0, false, false, false, false, true, true, true, startCondition, startOffset, flipStart) as Feature;
                if (feature != null) { lastSketch = sketch; return feature; }
                if (flipGiven) break;
                flip = !flip;
            }
            throw new SwFailure("extrude_failed", "SolidWorks could not extrude the sketch '" + sketch + "'.");
        }

        static Feature Cut(ModelDoc2 doc, Dictionary<string, object> step, ref string lastSketch)
        {
            string sketch = StepStr(step, "sketch", lastSketch);
            if (string.IsNullOrEmpty(sketch))
                throw new SwFailure("bad_request", "No sketch to cut with: add a sketch step first, or pass sketch with its name.");
            bool throughAll = StepBool(step, "throughAll", !step.ContainsKey("depth"));
            bool both = StepBool(step, "bothDirections", false);
            double depth = StepDouble(step, "depth", 0) / 1000.0;
            if (!throughAll && depth <= 0)
                throw new SwFailure("bad_request", "A blind cut needs a positive depth (mm), or throughAll=true.");
            bool flip = StepBool(step, "flip", false);
            bool flipGiven = step.ContainsKey("flip");
            int endCondition = throughAll
                ? (both ? (int)swEndConditions_e.swEndCondThroughAllBoth : (int)swEndConditions_e.swEndCondThroughAll)
                : (int)swEndConditions_e.swEndCondBlind;
            double offset = StepDouble(step, "offset", 0) / 1000.0;
            int startCondition = Math.Abs(offset) > 1e-9
                ? (int)swStartConditions_e.swStartOffset
                : (int)swStartConditions_e.swStartSketchPlane;
            double startOffset = Math.Abs(offset);
            bool flipStart = offset < 0;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                SelectSketch(doc, sketch);
                Feature feature = doc.FeatureManager.FeatureCut4(true, false, flip, endCondition, 0, depth, 0.0,
                    false, false, false, false, 0, 0, false, false, false, false, false, true, true, false, true, false, startCondition, startOffset, flipStart, true) as Feature;
                if (feature != null) { lastSketch = sketch; return feature; }
                if (flipGiven) break;
                flip = !flip;
            }
            throw new SwFailure("cut_failed",
                "SolidWorks could not cut with the sketch '" + sketch + "'. Check that the sketch is on the part and that its profile crosses material.");
        }

        static Feature Fillet(ModelDoc2 doc, Dictionary<string, object> step)
        {
            double radius = StepDouble(step, "radius", 0) / 1000.0;
            if (radius <= 0) throw new SwFailure("bad_request", "Fillet needs a positive radius in mm.");
            int edges = SelectEdgesForStep(doc, step);
            if (edges == 0) throw new SwFailure("no_edges", "The part has no edges to round.");
            object raw = doc.FeatureManager.FeatureFillet3(2, radius, 0, 0, 0, 0, 0,
                null, null, null, null, null, null, null);
            if (raw == null)
                throw new SwFailure("fillet_failed",
                    "SolidWorks could not round those edges with a " + StepDouble(step, "radius", 0) + " mm radius; the radius may be too large for the geometry.");
            return (Feature)raw;
        }

        static Feature Chamfer(ModelDoc2 doc, Dictionary<string, object> step)
        {
            double distance = StepDouble(step, "distance", 0) / 1000.0;
            if (distance <= 0) throw new SwFailure("bad_request", "Chamfer needs a positive distance in mm.");
            int edges = SelectEdgesForStep(doc, step);
            if (edges == 0) throw new SwFailure("no_edges", "The part has no edges to chamfer.");
            // swChamferEqualDistance is accepted but removes nothing through the API in 2019;
            // distance-distance with equal legs gives the same shape.
            object raw = doc.FeatureManager.InsertFeatureChamfer(0, (int)swChamferType_e.swChamferDistanceDistance, distance, 0, distance, 0, 0, 0);
            if (raw == null)
                throw new SwFailure("chamfer_failed",
                    "SolidWorks could not chamfer those edges with " + StepDouble(step, "distance", 0) + " mm.");
            return (Feature)raw;
        }

        static int SelectAllEdges(ModelDoc2 doc)
        {
            doc.ClearSelection2(true);
            int count = 0;
            foreach (Edge edge in CollectEdges(doc))
            {
                IEntity entity = edge as IEntity;
                if (entity == null) continue;
                entity.Select4(count > 0, null);
                count++;
            }
            return count;
        }

        static List<Edge> CollectEdges(ModelDoc2 doc)
        {
            var list = new List<Edge>();
            PartDoc part = doc as PartDoc;
            if (part == null) return list;
            object[] bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            foreach (object rawBody in bodies ?? new object[0])
            {
                Body2 body = rawBody as Body2;
                if (body == null) continue;
                object[] edges = body.GetEdges() as object[];
                foreach (object rawEdge in edges ?? new object[0])
                {
                    Edge edge = rawEdge as Edge;
                    if (edge != null) list.Add(edge);
                }
            }
            return list;
        }

        /** Selects the edges named by edgeIndices (1-based, as solidworks_inspect lists them), or all edges. */
        static int SelectEdgesForStep(ModelDoc2 doc, Dictionary<string, object> step)
        {
            object[] indices = step.ContainsKey("edgeIndices") ? AsArray(step["edgeIndices"]) : null;
            if (indices == null || indices.Length == 0) return SelectAllEdges(doc);

            doc.ClearSelection2(true);
            List<Edge> edges = CollectEdges(doc);
            int selected = 0;
            foreach (object raw in indices)
            {
                int number = (int)AsDouble(raw);
                if (number < 1 || number > edges.Count)
                    throw new SwFailure("bad_request", "edgeIndices must be between 1 and " + edges.Count +
                        "; call solidworks_inspect with edges=true to list them.");
                IEntity entity = edges[number - 1] as IEntity;
                if (entity == null) continue;
                entity.Select4(selected > 0, null);
                selected++;
            }
            if (selected == 0) throw new SwFailure("no_edges", "None of the requested edges could be selected.");
            return selected;
        }

        static Feature Revolve(ModelDoc2 doc, Dictionary<string, object> step, ref string lastSketch)
        {
            string sketch = StepStr(step, "sketch", lastSketch);
            if (string.IsNullOrEmpty(sketch))
                throw new SwFailure("bad_request", "No sketch to revolve: add a sketch step first, or pass sketch with its name.");
            double angle = StepDouble(step, "angle", 360) * Math.PI / 180.0;
            if (angle <= 0 || angle > 2 * Math.PI + 1e-6)
                throw new SwFailure("bad_request", "Revolve angle must be between 1 and 360 degrees.");
            bool flip = StepBool(step, "flip", false);

            SelectSketch(doc, sketch);
            Feature feature = doc.FeatureManager.FeatureRevolve2(true, true, false, false, flip, false,
                0, 0, angle, 0, false, false, 0, 0, 0, 0, 0, true, true, true) as Feature;
            if (feature == null)
                throw new SwFailure("revolve_failed",
                    "SolidWorks could not revolve '" + sketch + "'. Draw a centerline plus a closed profile beside it, then revolve.");
            lastSketch = sketch;
            return feature;
        }

        static Feature Loft(ModelDoc2 doc, Dictionary<string, object> step)
        {
            var profiles = new List<string>();
            object[] raw = step.ContainsKey("profiles") ? AsArray(step["profiles"]) : null;
            if (raw != null)
            {
                foreach (object item in raw)
                {
                    string name = Convert.ToString(item, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(name)) profiles.Add(name);
                }
            }
            if (profiles.Count < 2)
            {
                List<string> sketches = SketchNames(doc);
                if (sketches.Count < 2)
                    throw new SwFailure("bad_request", "A loft needs at least two sketches on different planes (or profiles with two names).");
                profiles = sketches.GetRange(sketches.Count - 2, 2);
            }

            doc.ClearSelection2(true);
            for (int i = 0; i < profiles.Count; i++)
            {
                if (!doc.Extension.SelectByID2(profiles[i], "SKETCH", 0, 0, 0, i > 0, 0, null, 0))
                    throw new SwFailure("no_sketch", "Could not select the loft profile '" + profiles[i] + "'.");
            }

            Feature feature = doc.FeatureManager.InsertProtrusionBlend2(false, false, false, 1, 0, 0, 0, 0,
                false, false, false, 0, 0, 0, true, true, true, 0) as Feature;
            if (feature == null)
                throw new SwFailure("loft_failed",
                    "SolidWorks could not loft those sketches. Profiles must be closed and on parallel planes; check their order.");
            return feature;
        }

        static Feature Sweep(ModelDoc2 doc, Dictionary<string, object> step, ref string lastSketch)
        {
            List<string> sketches = SketchNames(doc);
            string profile = StepStr(step, "profile", null);
            string path = StepStr(step, "path", null);
            if (string.IsNullOrEmpty(profile) && sketches.Count >= 2) profile = sketches[sketches.Count - 1];
            if (string.IsNullOrEmpty(path) && sketches.Count >= 2) path = sketches[sketches.Count - 2];
            if (string.IsNullOrEmpty(profile) || string.IsNullOrEmpty(path))
                throw new SwFailure("bad_request",
                    "A sweep needs a profile sketch and a path sketch; create both first, or pass profile and path with their names.");

            doc.ClearSelection2(true);
            if (!doc.Extension.SelectByID2(profile, "SKETCH", 0, 0, 0, false, 0, null, 0))
                throw new SwFailure("no_sketch", "Could not select the sweep profile '" + profile + "'.");
            if (!doc.Extension.SelectByID2(path, "SKETCH", 0, 0, 0, true, 0, null, 0))
                throw new SwFailure("no_sketch", "Could not select the sweep path '" + path + "'.");

            doc.InsertProtrusionSwept4(true, false, 0, false, false, 0, 0, false, 0, 0, 0);
            Feature feature = doc.Extension.GetLastFeatureAdded();
            if (feature == null || feature.GetTypeName2() != "Sweep")
                throw new SwFailure("sweep_failed",
                    "SolidWorks could not sweep along '" + path + "'. The path must start on the profile plane and be a single continuous curve.");
            lastSketch = profile;
            return feature;
        }

        static Feature Shell(ModelDoc2 doc, Dictionary<string, object> step)
        {
            double thickness = StepDouble(step, "thickness", 2) / 1000.0;
            if (thickness <= 0) throw new SwFailure("bad_request", "Shell needs a positive thickness in mm.");
            bool outward = StepBool(step, "outward", false);
            string face = StepStr(step, "faces", "top").ToLowerInvariant();

            doc.ClearSelection2(true);
            if (face != "none")
            {
                double[] direction = DirectionOf(face);
                Face2 opening = LargestFace(doc, direction);
                if (opening == null)
                    throw new SwFailure("no_face", "No face of the part points " + face + "; pass faces=none for a closed shell.");
                ((IEntity)opening).Select4(false, null);
            }

            doc.InsertFeatureShell(thickness, outward);
            Feature feature = doc.Extension.GetLastFeatureAdded();
            if (feature == null || feature.GetTypeName2() != "Shell")
                throw new SwFailure("shell_failed", "SolidWorks could not shell the part with " + StepDouble(step, "thickness", 2) + " mm.");
            return feature;
        }

        static Feature SurfaceExtrude(ModelDoc2 doc, Dictionary<string, object> step, ref string lastSketch)
        {
            string sketch = StepStr(step, "sketch", lastSketch);
            if (string.IsNullOrEmpty(sketch))
                throw new SwFailure("bad_request", "No sketch to extrude as a surface: add a sketch step first, or pass sketch with its name.");
            double depth = StepDouble(step, "depth", 20) / 1000.0;
            if (depth <= 0) throw new SwFailure("bad_request", "Surface extrude needs a positive depth in mm.");
            bool flip = StepBool(step, "flip", false);

            SelectSketch(doc, sketch);
            doc.FeatureManager.FeatureExtruRefSurface3(true, flip, 0, 0, 0, 0, depth, 0.0,
                false, false, false, false, 0, 0, false, false, false, false, false, false, false, false);
            Feature feature = doc.Extension.GetLastFeatureAdded();
            if (feature == null || feature.GetTypeName2() != "ExtruRefSurface")
                throw new SwFailure("surface_failed",
                    "SolidWorks could not extrude '" + sketch + "' as a surface. Use an open or closed sketch, not a profile that already forms a solid.");
            lastSketch = sketch;
            return feature;
        }

        static List<string> SketchNames(ModelDoc2 doc)
        {
            var names = new List<string>();
            Feature feature = doc.FirstFeature() as Feature;
            while (feature != null)
            {
                string type = "";
                try { type = feature.GetTypeName2() ?? ""; } catch { }
                if (type == "ProfileFeature") names.Add(feature.Name);
                feature = feature.GetNextFeature() as Feature;
            }
            return names;
        }

        static double[] DirectionOf(string face)
        {
            switch (face)
            {
                case "top": return new double[] { 0, 0, 1 };
                case "bottom": return new double[] { 0, 0, -1 };
                case "front": return new double[] { 0, -1, 0 };
                case "back": return new double[] { 0, 1, 0 };
                case "right": return new double[] { 1, 0, 0 };
                case "left": return new double[] { -1, 0, 0 };
                default: throw new SwFailure("bad_request", "faces must be top, bottom, front, back, left, right or none.");
            }
        }

        static Face2 LargestFace(ModelDoc2 doc, double[] direction)
        {
            PartDoc part = doc as PartDoc;
            if (part == null) return null;
            object[] bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            Face2 best = null;
            double bestArea = 0;
            foreach (object rawBody in bodies ?? new object[0])
            {
                Body2 body = rawBody as Body2;
                if (body == null) continue;
                object[] faces = body.GetFaces() as object[];
                foreach (object rawFace in faces ?? new object[0])
                {
                    Face2 candidate = rawFace as Face2;
                    if (candidate == null) continue;
                    double[] normal = candidate.Normal as double[];
                    if (normal == null || normal.Length < 3) continue;
                    double dot = normal[0] * direction[0] + normal[1] * direction[1] + normal[2] * direction[2];
                    if (dot < 0.98) continue;
                    double area = candidate.GetArea();
                    if (area > bestArea) { bestArea = area; best = candidate; }
                }
            }
            return best;
        }

        // ------------------------------------------------------------- assembly

        static object AssemblyBuild()
        {
            ModelDoc2 doc = ResolveDoc(true);
            if (doc.GetType() != 2)
                throw new SwFailure("unsupported", "Assembly steps work on assemblies only. Open or create an assembly first.");
            AssemblyDoc assembly = doc as AssemblyDoc;
            if (assembly == null) throw new SwFailure("unsupported", "SolidWorks did not return an assembly interface.");

            var data = new Dictionary<string, object>();
            var notes = new List<string>();
            string backup = GuardForEdit(doc, notes, "Assembly editing");

            var steps = request.ContainsKey("steps") ? AsArray(request["steps"]) : null;
            if (steps == null || steps.Length == 0)
                throw new SwFailure("bad_request", "Pass a steps array with at least one assembly step.");

            var results = new List<object>();
            int index = 0;

            foreach (object rawStep in steps)
            {
                index++;
                Dictionary<string, object> step = rawStep as Dictionary<string, object>;
                if (step == null) throw new SwFailure("bad_request", "Step " + index + " is not an object.");
                string op = StepStr(step, "op", "").ToLowerInvariant();
                string featureName = "";
                try
                {
                    switch (op)
                    {
                        case "insert":
                        {
                            string path = StepStr(step, "path", null);
                            if (string.IsNullOrEmpty(path)) throw new SwFailure("bad_request", "insert needs path with the part or assembly file to insert.");
                            if (!File.Exists(path)) throw new SwFailure("not_found", "The file does not exist: " + path);
                            EnsureDocOpen(path);
                            Component2 component = assembly.AddComponent5(
                                path,
                                (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                                StepStr(step, "configuration", ""), false, "",
                                StepDouble(step, "x", 0) / 1000.0,
                                StepDouble(step, "y", 0) / 1000.0,
                                StepDouble(step, "z", 0) / 1000.0);
                            if (component == null)
                                throw new SwFailure("insert_failed",
                                    "SolidWorks could not insert '" + path + "' into the assembly. Make sure the file is a part or assembly and is not already inserted under the same name.");
                            if (StepBool(step, "fix", false)) SetComponentFixed(doc, assembly, component, true);
                            featureName = component.Name2 ?? "";
                            break;
                        }
                        case "mate":
                        {
                            string type = StepStr(step, "type", "coincident").ToLowerInvariant();
                            Component2 first = FindComponent(assembly, StepStr(step, "componentA", null));
                            Component2 second = FindComponent(assembly, StepStr(step, "componentB", null));
                            if (first == null || second == null)
                                throw new SwFailure("not_found",
                                    "mate needs componentA and componentB with the instance names from this assembly (" + ComponentNames(assembly) + ").");
                            string planeA = StepStr(step, "planeA", "front");
                            string planeB = StepStr(step, "planeB", "front");
                            string referenceA = ComponentPlaneReference(doc, first, BasePlaneAlias(planeA));
                            string referenceB = ComponentPlaneReference(doc, second, BasePlaneAlias(planeB));
                            doc.ClearSelection2(true);
                            if (!doc.Extension.SelectByID2(referenceA, "PLANE", 0, 0, 0, false, 0, null, 0))
                                throw new SwFailure("no_plane", "Could not select the " + planeA + " plane of '" + first.Name2 + "' (" + referenceA + ").");
                            if (!doc.Extension.SelectByID2(referenceB, "PLANE", 0, 0, 0, true, 0, null, 0))
                                throw new SwFailure("no_plane", "Could not select the " + planeB + " plane of '" + second.Name2 + "' (" + referenceB + ").");

                            double distance = StepDouble(step, "distance", 0) / 1000.0;
                            double angle = StepDouble(step, "angle", 0) * Math.PI / 180.0;
                            bool flip = StepBool(step, "flip", false);
                            bool antiAligned = flip;
                            int error = 0;
                            Mate2 mate = assembly.AddMate5(
                                MateTypeOf(type),
                                antiAligned ? (int)swMateAlign_e.swMateAlignANTI_ALIGNED : (int)swMateAlign_e.swMateAlignCLOSEST,
                                false, distance, distance, distance,
                                0, 0, angle, angle, angle,
                                false, false, 0, out error);
                            if (mate == null)
                            {
                                int selectedCount = -1;
                                try { selectedCount = ((ISelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1); } catch { }
                                throw new SwFailure("mate_failed",
                                    "SolidWorks could not create the " + type + " mate (error " + error + ", selected " + selectedCount + "). " +
                                    "A distance or angle mate needs its value; components must not already be fixed in a conflicting position.");
                            }
                            featureName = type + " mate";
                            break;
                        }
                        case "fix":
                        case "float":
                        {
                            Component2 component = FindComponent(assembly, StepStr(step, "component", null));
                            if (component == null)
                                throw new SwFailure("not_found", op + " needs component with an instance name (" + ComponentNames(assembly) + ").");
                            SetComponentFixed(doc, assembly, component, op == "fix");
                            featureName = (op == "fix" ? "fixed " : "floated ") + component.Name2;
                            break;
                        }
                        default:
                            throw new SwFailure("bad_request", "Unknown assembly step '" + op + "'. Use insert, mate, fix or float.");
                    }
                }
                catch (SwFailure failure)
                {
                    throw new SwFailure(failure.Kind, "Step " + index + " (" + op + "): " + failure.Message);
                }
                results.Add(new Dictionary<string, object> { { "step", index }, { "op", op }, { "feature", featureName } });
            }

            doc.ForceRebuild3(false);
            int activeErrors = 0;
            try { App(false).ActivateDoc3(doc.GetTitle(), false, 0, ref activeErrors); } catch { }
            try { doc.ShowNamedView2("*Isometric", (int)swStandardViews_e.swIsometricView); } catch { }

            data["doc"] = DocInfo(doc, App(false).ActiveDoc as ModelDoc2);
            data["steps"] = results;
            data["backup"] = backup;
            data["notes"] = notes;
            data["components"] = ComponentList(assembly);
            data["componentCount"] = assembly.GetComponentCount(true);
            data["mass"] = ReadMass(doc);

            if (Bool("preview", true))
            {
                try
                {
                    string folder = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "dsh-solidworks", "captures");
                    Directory.CreateDirectory(folder);
                    string name = (doc.GetTitle() ?? "assembly").Replace("/", "-").Replace("\\", "-").Replace(":", "-");
                    string outPath = Path.Combine(folder, name + "-assembly-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".png");
                    CaptureClean(doc, outPath, 1400, 900);
                    data["previewPng"] = outPath;
                }
                catch (Exception ex)
                {
                    notes.Add("Preview could not be captured: " + Describe(ex));
                }
            }

            return data;
        }

        static List<object> ComponentList(AssemblyDoc assembly)
        {
            var list = new List<object>();
            object[] components = assembly.GetComponents(true) as object[];
            foreach (object raw in components ?? new object[0])
            {
                Component2 component = raw as Component2;
                if (component == null) continue;
                var entry = new Dictionary<string, object>
                {
                    { "name", component.Name2 ?? "" },
                    { "path", component.GetPathName() ?? "" },
                    { "configuration", component.ReferencedConfiguration ?? "" }
                };
                try { entry["fixed"] = component.IsFixed(); } catch { }
                try { entry["suppressed"] = component.IsSuppressed(); } catch { }
                list.Add(entry);
            }
            return list;
        }

        static string ComponentNames(AssemblyDoc assembly)
        {
            var names = new List<string>();
            object[] components = assembly.GetComponents(true) as object[];
            foreach (object raw in components ?? new object[0])
            {
                Component2 component = raw as Component2;
                if (component != null) names.Add(component.Name2);
            }
            return names.Count == 0 ? "none" : string.Join(", ", names.ToArray());
        }

        static Component2 FindComponent(AssemblyDoc assembly, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string wanted = name.Trim();
            object[] components = assembly.GetComponents(true) as object[];
            foreach (object raw in components ?? new object[0])
            {
                Component2 component = raw as Component2;
                if (component == null) continue;
                if (string.Equals(component.Name2, wanted, StringComparison.OrdinalIgnoreCase)) return component;
                string path = component.GetPathName() ?? "";
                if (string.Equals(path, wanted, StringComparison.OrdinalIgnoreCase)) return component;
                if (!string.IsNullOrEmpty(path) && string.Equals(Path.GetFileName(path), wanted, StringComparison.OrdinalIgnoreCase)) return component;
            }
            return null;
        }

        static void EnsureDocOpen(string path)
        {
            string full = SafePath(path);
            foreach (ModelDoc2 model in OpenDocs(App(false)))
                if (string.Equals(SafePath(model.GetPathName()), full, StringComparison.OrdinalIgnoreCase)) return;

            int errors = 0;
            int warnings = 0;
            ModelDoc2 opened = App(false).OpenDoc6(path, TypeOfPath(path), (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
            if (opened == null)
                throw new SwFailure("open_failed", "Could not load '" + path + "' to insert it (error " + errors + ").");
        }

        static bool IsNegativePlane(string plane)
        {
            string lower = (plane ?? "").ToLowerInvariant();
            return lower == "bottom" || lower == "back" || lower == "left" ||
                   lower == "下视" || lower == "后视" || lower == "左视";
        }

        static string BasePlaneAlias(string plane)
        {
            switch ((plane ?? "front").ToLowerInvariant())
            {
                case "bottom":
                case "下视": return "top";
                case "back":
                case "后视": return "front";
                case "left":
                case "左视": return "right";
                default: return plane;
            }
        }

        static string ComponentPlaneName(Component2 component, string plane)
        {
            string wanted = string.IsNullOrEmpty(plane) ? "front" : plane.Trim();
            ModelDoc2 part = component.GetModelDoc2() as ModelDoc2;
            if (part != null)
            {
                try { return ResolvePlane(part, wanted); } catch (SwFailure) { }
            }
            switch (wanted.ToLowerInvariant())
            {
                case "front": case "前视": return "前视基准面";
                case "top": case "上视": return "上视基准面";
                case "right": case "右视": return "右视基准面";
                default: return wanted;
            }
        }

        /** The SelectByID2 reference of a component's plane. SolidWorks names it "<plane>@<component>@<assembly>"
         *  with the assembly name without its file extension. */
        static string ComponentPlaneReference(ModelDoc2 assemblyDoc, Component2 component, string plane)
        {
            string name = ComponentPlaneName(component, plane);
            string suffix = Path.GetFileNameWithoutExtension(assemblyDoc.GetTitle());
            if (string.IsNullOrEmpty(suffix)) suffix = assemblyDoc.GetTitle();
            return name + "@" + component.Name2 + "@" + suffix;
        }

        static void SetComponentFixed(ModelDoc2 assemblyDoc, AssemblyDoc assembly, Component2 component, bool fix)
        {
            assemblyDoc.ClearSelection2(true);
            string assemblyName = Path.GetFileNameWithoutExtension(assemblyDoc.GetTitle());
            if (string.IsNullOrEmpty(assemblyName)) assemblyName = assemblyDoc.GetTitle();
            string reference = component.Name2 + "@" + assemblyName;
            if (!assemblyDoc.Extension.SelectByID2(reference, "COMPONENT", 0, 0, 0, false, 0, null, 0))
                throw new SwFailure("not_found", "Could not select the component '" + component.Name2 + "'.");
            if (fix) assembly.FixComponent();
            else assembly.UnfixComponent();
        }

        static int MateTypeOf(string type)
        {
            switch ((type ?? "coincident").ToLowerInvariant())
            {
                case "coincident": return (int)swMateType_e.swMateCOINCIDENT;
                case "concentric": return (int)swMateType_e.swMateCONCENTRIC;
                case "perpendicular": return (int)swMateType_e.swMatePERPENDICULAR;
                case "parallel": return (int)swMateType_e.swMatePARALLEL;
                case "tangent": return (int)swMateType_e.swMateTANGENT;
                case "distance": return (int)swMateType_e.swMateDISTANCE;
                case "angle": return (int)swMateType_e.swMateANGLE;
                default:
                    throw new SwFailure("bad_request", "mate type must be coincident, concentric, perpendicular, parallel, tangent, distance or angle.");
            }
        }

        // -------------------------------------------------------------- drawing

        static object DrawingBuild()
        {
            string action = Str("action", "create").ToLowerInvariant();
            if (action == "views")
            {
                ModelDoc2 current = ResolveDoc(true);
                if (current.GetType() != 3)
                    throw new SwFailure("unsupported", "action=views needs an open drawing. Open a .slddrw first.");
                return DrawingViews(current as DrawingDoc, current);
            }

            string modelPath = Str("model", null);
            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
                throw new SwFailure("not_found", "Pass model with the path of an existing part or assembly file.");

            string template = Str("template", null);
            if (string.IsNullOrEmpty(template))
                template = App(false).GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateDrawing);
            if (string.IsNullOrEmpty(template) || !File.Exists(template))
                throw new SwFailure("no_template",
                    "SolidWorks has no default drawing template. Set one in Tools > Options > System Options > Default Templates, or pass template.");

            object raw = App(false).NewDrawing2((int)swDwgTemplates_e.swDwgTemplateCustom, template, 0, 0, 0);
            DrawingDoc drawing = raw as DrawingDoc;
            if (drawing == null)
                throw new SwFailure("drawing_failed", "SolidWorks did not create a drawing from '" + template + "'.");
            ModelDoc2 doc = drawing as ModelDoc2;
            doc.EditRebuild3();

            double sheetWidth = 0.42;
            double sheetHeight = 0.297;
            Sheet sheet = drawing.GetCurrentSheet() as Sheet;
            if (sheet != null)
            {
                double width = 0;
                double height = 0;
                try { sheet.GetSize(ref width, ref height); } catch { }
                if (width > 0.05 && height > 0.05) { sheetWidth = width; sheetHeight = height; }
            }

            var wantedNames = new List<string>();
            object[] rawViews = request.ContainsKey("views") ? AsArray(request["views"]) : null;
            if (rawViews != null)
            {
                foreach (object item in rawViews)
                {
                    string name = Convert.ToString(item, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(name)) wantedNames.Add(name.ToLowerInvariant());
                }
            }
            if (wantedNames.Count == 0) wantedNames.AddRange(new[] { "front", "top", "right", "iso" });

            var positions = new Dictionary<string, double[]>
            {
                { "front", new[] { 0.30, 0.45 } },
                { "top", new[] { 0.30, 0.72 } },
                { "right", new[] { 0.55, 0.45 } },
                { "left", new[] { 0.09, 0.45 } },
                { "back", new[] { 0.55, 0.72 } },
                { "bottom", new[] { 0.09, 0.72 } },
                { "iso", new[] { 0.78, 0.55 } }
            };

            var created = new List<object>();
            foreach (string view in wantedNames)
            {
                double[] position;
                if (!positions.TryGetValue(view, out position)) position = new[] { 0.5, 0.5 };
                View createdView = null;
                foreach (string candidate in DrawingViewNames(view))
                {
                    createdView = drawing.CreateDrawViewFromModelView3(modelPath, candidate, sheetWidth * position[0], sheetHeight * position[1], 0);
                    if (createdView != null) break;
                }
                if (createdView == null)
                    throw new SwFailure("view_failed",
                        "Could not create the '" + view + "' view. Use front, top, right, left, back, bottom or iso.");
                created.Add(new Dictionary<string, object> { { "name", createdView.GetName2() }, { "view", view } });
            }

            var notes = new List<string>();
            if (Bool("autoDimensions", true))
            {
                try
                {
                    drawing.InsertModelAnnotations3(0, (int)swInsertAnnotation_e.swInsertDimensions, true, true, true, false);
                    notes.Add("Model dimensions were inserted where the model had dimensions marked for drawing.");
                }
                catch { }
            }

            doc.ForceRebuild3(false);
            return new Dictionary<string, object>
            {
                { "doc", DocInfo(doc, App(false).ActiveDoc as ModelDoc2) },
                { "sheet", sheet != null ? sheet.GetName() : "" },
                { "views", created },
                { "notes", notes }
            };
        }

        static object DrawingViews(DrawingDoc drawing, ModelDoc2 doc)
        {
            var views = new List<object>();
            Sheet currentSheet = drawing.GetCurrentSheet() as Sheet;
            object[] raw = currentSheet != null ? AsArray(currentSheet.GetViews()) : AsArray(drawing.GetViews());
            foreach (object item in raw ?? new object[0])
            {
                View view = item as View;
                if (view == null) continue;
                var entry = new Dictionary<string, object>
                {
                    { "name", view.GetName2() },
                    { "type", view.Type }
                };
                try
                {
                    double[] position = view.Position as double[];
                    if (position != null && position.Length >= 2)
                        entry["position"] = new object[] { Num(position[0]), Num(position[1]) };
                }
                catch { }
                views.Add(entry);
            }
            return new Dictionary<string, object>
            {
                { "doc", DocInfo(doc, App(false).ActiveDoc as ModelDoc2) },
                { "sheet", currentSheet != null ? currentSheet.GetName() : "" },
                { "views", views },
                { "count", views.Count }
            };
        }

        static string[] DrawingViewNames(string view)
        {
            switch (view)
            {
                case "front": return new[] { "*前视", "*Front" };
                case "top": return new[] { "*上视", "*Top" };
                case "right": return new[] { "*右视", "*Right" };
                case "left": return new[] { "*左视", "*Left" };
                case "back": return new[] { "*后视", "*Back" };
                case "bottom": return new[] { "*下视", "*Bottom" };
                case "iso": return new[] { "*等轴测", "*Isometric" };
                default: return new[] { view };
            }
        }

        static void SelectSketch(ModelDoc2 doc, string sketch)
        {
            doc.ClearSelection2(true);
            if (!doc.Extension.SelectByID2(sketch, "SKETCH", 0, 0, 0, false, 0, null, 0))
                throw new SwFailure("no_sketch", "Could not select the sketch '" + sketch + "'. Open documents: " + DescribeOpenDocs(App(false)));
        }

        static string ResolvePlane(ModelDoc2 doc, string plane)
        {
            var planes = new List<Feature>();
            Feature feature = doc.FirstFeature() as Feature;
            while (feature != null)
            {
                if (feature.GetTypeName2() == "RefPlane") planes.Add(feature);
                feature = feature.GetNextFeature() as Feature;
            }
            if (planes.Count == 0) throw new SwFailure("no_plane", "This part has no reference planes.");

            string wanted = string.IsNullOrEmpty(plane) ? "front" : plane.Trim();
            foreach (Feature candidate in planes)
                if (string.Equals(candidate.Name, wanted, StringComparison.OrdinalIgnoreCase)) return candidate.Name;

            string lower = wanted.ToLowerInvariant();
            if (lower == "front" || lower == "前视") return PlaneByKeyword(planes, new[] { "front", "前视" }, 0);
            if (lower == "top" || lower == "上视") return PlaneByKeyword(planes, new[] { "top", "上视" }, 1);
            if (lower == "right" || lower == "右视") return PlaneByKeyword(planes, new[] { "right", "右视" }, 2);

            int number;
            if (int.TryParse(wanted, out number) && number >= 1 && number <= planes.Count) return planes[number - 1].Name;

            var names = new List<string>();
            foreach (Feature candidate in planes) names.Add(candidate.Name);
            throw new SwFailure("no_plane",
                "No reference plane matches '" + plane + "'. Use front/top/right, a plane name, or an index. Planes: " + string.Join(", ", names.ToArray()));
        }

        static string PlaneByKeyword(List<Feature> planes, string[] keywords, int fallbackIndex)
        {
            foreach (Feature candidate in planes)
            {
                string name = (candidate.Name ?? "").ToLowerInvariant();
                foreach (string keyword in keywords)
                    if (name.Contains(keyword)) return candidate.Name;
            }
            if (fallbackIndex < planes.Count) return planes[fallbackIndex].Name;
            return planes[0].Name;
        }

        static Feature LastFeature(ModelDoc2 doc, string typeName)
        {
            Feature found = null;
            Feature feature = doc.FirstFeature() as Feature;
            while (feature != null)
            {
                if (feature.GetTypeName2() == typeName) found = feature;
                feature = feature.GetNextFeature() as Feature;
            }
            return found;
        }

        static List<object> FeatureNames(ModelDoc2 doc)
        {
            var names = new List<object>();
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "FavoriteFolder", "HistoryFolder", "SelectionSetFolder", "SensorFolder", "DocsFolder",
                "DetailCabinet", "InkMarkupFolder", "EnvFolder", "SolidBodyFolder", "SurfaceBodyFolder",
                "CommentsFolder", "EqnFolder", "MaterialFolder", "OriginProfileFeature"
            };
            Feature feature = doc.FirstFeature() as Feature;
            int guard = 0;
            while (feature != null && guard < 300)
            {
                guard++;
                string type = "";
                try { type = feature.GetTypeName2() ?? ""; } catch { }
                if (!skip.Contains(type))
                    names.Add(new Dictionary<string, object> { { "name", feature.Name ?? "" }, { "type", type } });
                feature = feature.GetNextFeature() as Feature;
            }
            return names;
        }

        // Step field helpers ---------------------------------------------------

        static string StepStr(Dictionary<string, object> step, string key, string fallback)
        {
            object value;
            if (step != null && step.TryGetValue(key, out value) && value != null)
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            return fallback;
        }

        static bool StepBool(Dictionary<string, object> step, string key, bool fallback)
        {
            object value;
            if (step != null && step.TryGetValue(key, out value) && value != null)
            {
                if (value is bool) return (bool)value;
                bool parsed;
                if (bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed)) return parsed;
            }
            return fallback;
        }

        static double StepDouble(Dictionary<string, object> step, string key, double fallback)
        {
            object value;
            if (step != null && step.TryGetValue(key, out value) && value != null)
                return AsDouble(value);
            return fallback;
        }

        static double AsDouble(object value)
        {
            if (value == null) return 0;
            if (value is double) return (double)value;
            if (value is int) return (int)value;
            if (value is long) return (long)value;
            if (value is decimal) return (double)(decimal)value;
            double parsed;
            if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return 0;
        }

        static object[] AsArray(object value)
        {
            object[] array = value as object[];
            if (array != null) return array;
            System.Collections.IEnumerable enumerable = value as System.Collections.IEnumerable;
            if (enumerable == null || value is string) return null;
            var items = new List<object>();
            foreach (object item in enumerable) items.Add(item);
            return items.ToArray();
        }

        static double[] StepVector(Dictionary<string, object> step, string key)
        {
            object value;
            if (step != null && step.TryGetValue(key, out value))
            {
                object[] items = AsArray(value);
                if (items != null && items.Length >= 2)
                    return new double[] { AsDouble(items[0]), AsDouble(items[1]) };
            }
            return new double[] { 0, 0 };
        }

        // ----------------------------------------------------------------- misc

        static object Num(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return null;
            return value;
        }
    }
}











