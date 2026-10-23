import z from "@deepseek-ai/schemastery";
import { readFile, rm } from "node:fs/promises";
import { basename } from "node:path";
import { SolidWorksDriver } from "./lib/runner.js";
import * as render from "./lib/format.js";

export const name = "dsh-solidworks";
export const inject = ["tools", "attachments"];

const zh = (text) => ({ "zh-CN": { $description: text } });
const en = (text) => ({ "en-US": { $description: text } });

export const Config = z.object({
  visible: z
    .boolean()
    .default(false)
    .volatile()
    .i18n({
      ...zh("显示 SolidWorks 窗口；默认关闭，在后台无界面运行（建模、导出、截图都不受影响）"),
      ...en("Show the SolidWorks window; off by default, so it runs in the background with no window (modeling, exports and captures still work)"),
    }),
  autoLaunch: z
    .boolean()
    .default(true)
    .volatile()
    .i18n({
      ...zh("需要时自动启动 SolidWorks（首次约 30 秒）；关闭后只操作已经打开的 SolidWorks"),
      ...en("Start SolidWorks when a tool needs it (about 30 s on the first call); off = only talk to a running SolidWorks"),
    }),
  timeoutSec: z
    .natural()
    .min(30)
    .max(900)
    .default(180)
    .volatile()
    .i18n({
      ...zh("单次自动化操作的最长等待时间（秒），大装配体打开或导出时需要更久"),
      ...en("Longest wait for one automation call in seconds; large assemblies and exports need more"),
    }),
  captureWidth: z
    .natural()
    .min(320)
    .max(3840)
    .default(1400)
    .volatile()
    .i18n({
      ...zh("截图宽度（像素）"),
      ...en("Screenshot width in pixels"),
    }),
  captureHeight: z
    .natural()
    .min(240)
    .max(2160)
    .default(900)
    .volatile()
    .i18n({
      ...zh("截图高度（像素）"),
      ...en("Screenshot height in pixels"),
    }),
  exePath: z
    .string()
    .default("")
    .volatile()
    .i18n({
      ...zh("SLDWORKS.exe 的完整路径；留空时自动从 COM 注册表查找"),
      ...en("Full path of SLDWORKS.exe; empty = look it up in the COM registration"),
    }),
  interopDir: z
    .string()
    .default("")
    .volatile()
    .i18n({
      ...zh("SolidWorks 互操作程序集目录（api\\redist）；留空时自动查找"),
      ...en("Folder with the SolidWorks interop assemblies (api\\redist); empty = auto-detect"),
    }),
});

const IMAGE = {
  type: "object",
  additionalProperties: false,
  properties: {
    attachmentId: { type: "string" },
    mediaType: { type: "string" },
    bytes: { type: "integer" },
    width: { type: "integer" },
    height: { type: "integer" },
    name: { type: "string" },
    originalDimensions: {
      type: "object",
      additionalProperties: false,
      properties: {
        width: { type: "integer" },
        height: { type: "integer" },
      },
    },
  },
};

const textOnly = {
  schema: {
    type: "object",
    additionalProperties: false,
    properties: { text: { type: "string" } },
  },
  render: (_args, value) => [{ type: "text", text: value.text }],
};

const withImage = {
  schema: {
    type: "object",
    additionalProperties: false,
    properties: {
      text: { type: "string" },
      images: { type: "array", items: IMAGE },
    },
  },
  render: (_args, value) => [
    { type: "text", text: value.text },
    ...(value.images ?? []).map((image) => ({ type: "image", attachment: image })),
  ],
};

function toJson(ref) {
  return {
    attachmentId: String(ref.attachmentId),
    mediaType: ref.mediaType,
    bytes: ref.bytes,
    width: ref.width,
    height: ref.height,
    ...(ref.name === undefined ? {} : { name: ref.name }),
    ...(ref.originalDimensions === undefined
      ? {}
      : { originalDimensions: { width: ref.originalDimensions.width, height: ref.originalDimensions.height } }),
  };
}

const DOC_PATH = {
  type: "string",
  description: "Absolute path of an open SolidWorks document. Omit both path and title to use the active document.",
};
const DOC_TITLE = {
  type: "string",
  description: "Window title of an open SolidWorks document (for example \"Bracket.SLDPRT\"), as an alternative to path.",
};

export function apply(ctx, config = {}) {
  const settings = {
    visible: typeof config.visible === "boolean" ? config.visible : false,
    autoLaunch: typeof config.autoLaunch === "boolean" ? config.autoLaunch : true,
    timeoutSec: Number.isFinite(config.timeoutSec) && config.timeoutSec > 0 ? config.timeoutSec : 180,
    captureWidth: Number.isFinite(config.captureWidth) && config.captureWidth > 0 ? config.captureWidth : 1400,
    captureHeight: Number.isFinite(config.captureHeight) && config.captureHeight > 0 ? config.captureHeight : 900,
    exePath: typeof config.exePath === "string" ? config.exePath : "",
    interopDir: typeof config.interopDir === "string" ? config.interopDir : "",
  };
  const driver = new SolidWorksDriver(settings);
  const base = {
    launch: settings.autoLaunch,
    visible: settings.visible,
    timeoutSec: settings.timeoutSec,
    exePath: settings.exePath || null,
    interopDir: settings.interopDir || null,
  };
  const toolTimeout = Math.max(30, settings.timeoutSec) * 1000 + 60000;
  const call = (request, exec) => driver.run({ ...base, ...request }, { signal: exec?.signal });

  const tools = [
    {
      name: "solidworks_status",
      description:
        "Report whether SolidWorks is installed and running on this Windows PC, its version, and the documents open in it (title, path, type, unsaved changes, which one is active). Does not start SolidWorks and does not change anything. Call it before other solidworks_ tools when you are unsure what is open.",
      parameters: { type: "object", properties: {}, additionalProperties: false },
      output: textOnly,
      timeoutMs: toolTimeout,
      async execute(_args, exec) {
        const data = await call({ op: "status", attach: true, launch: false }, exec);
        return { text: render.status(data) };
      },
    },
    {
      name: "solidworks_open",
      description:
        "Open a SolidWorks file (.sldprt, .sldasm, .slddrw) in SolidWorks so the user can watch and edit alongside, or create a new part, assembly or drawing from SolidWorks' default template. An already open document is reused. Starts SolidWorks when it is not running (about 30 seconds on the first call). Follow with solidworks_inspect to learn what the model contains.",
      parameters: {
        type: "object",
        properties: {
          path: { type: "string", description: "Absolute path of the SolidWorks file to open." },
          kind: {
            type: "string",
            enum: ["part", "assembly", "drawing"],
            description: "Create a new document of this type instead of opening a file.",
          },
          saveAs: {
            type: "string",
            description: "For a new document: save it to this absolute path immediately (the extension follows the kind).",
          },
          readOnly: { type: "boolean", description: "Open the file read-only." },
          activate: { type: "boolean", description: "Make the opened document the active one (default true)." },
        },
        additionalProperties: false,
      },
      output: textOnly,
      timeoutMs: toolTimeout,
      async execute(args, exec) {
        const data = await call(
          { op: "open", ...args, timeoutSec: Math.max(settings.timeoutSec, 120) },
          exec,
        );
        return { text: render.opened(data) };
      },
    },
    {
      name: "solidworks_documents",
      description:
        "List the documents open in SolidWorks right now: title, type (part/assembly/drawing), file path, which one is active, and which have unsaved changes.",
      parameters: { type: "object", properties: {}, additionalProperties: false },
      output: textOnly,
      timeoutMs: toolTimeout,
      async execute(_args, exec) {
        const data = await call({ op: "documents" }, exec);
        return { text: render.documents(data) };
      },
    },
    {
      name: "solidworks_inspect",
      description:
        "Summarize one open SolidWorks document: configurations, material, mass properties (kg, volume, surface, centre of mass), bounding box, solid and surface body counts, the feature tree, assembly components or drawing sheets, and the custom properties. With edges=true it also numbers every edge of a part, so a later fillet or chamfer can target just those edges. Units are millimetres and kilograms. This only reads the model.",
      parameters: {
        type: "object",
        properties: {
          path: DOC_PATH,
          title: DOC_TITLE,
          edges: {
            type: "boolean",
            description: "Part only: list every edge with its index, endpoints and chord length.",
          },
        },
        additionalProperties: false,
      },
      output: textOnly,
      timeoutMs: toolTimeout,
      async execute(args, exec) {
        const data = await call({ op: "inspect", ...args }, exec);
        return { text: render.inspect(data) };
      },
    },
    {
      name: "solidworks_properties",
      description:
        "Read or change the custom properties (the metadata tables: part number, description, material, revision, ...) of an open SolidWorks document, for the document or for one configuration. action=get returns them all; action=set writes the given name/value pairs; action=delete removes the named ones. Changing a saved file needs allowEdit=true and is backed up first.",
      parameters: {
        type: "object",
        properties: {
          action: { type: "string", enum: ["get", "set", "delete"], description: "What to do. Default: get." },
          path: DOC_PATH,
          title: DOC_TITLE,
          allowEdit: {
            type: "boolean",
            description: "Required to change a document that is already saved to disk: the original file is backed up first.",
          },
          configuration: {
            type: "string",
            description: "Configuration name for configuration-specific properties; omit for the document level.",
          },
          properties: {
            type: "object",
            description: "Name/value pairs to write (set), or name/value pairs whose names are removed (delete).",
            additionalProperties: true,
          },
        },
        additionalProperties: false,
      },
      output: textOnly,
      timeoutMs: toolTimeout,
      async execute(args, exec) {
        const data = await call({ op: "properties", ...args }, exec);
        return { text: render.properties(data) };
      },
    },
    {
      name: "solidworks_model",
      description:
        "Build or modify a part with parametric steps: draw a sketch on a reference plane (rectangle / circle / polygon / polyline / centreline, millimetres), extrude, cut, revolve, loft, sweep, shell or extrude a surface, then round or chamfer the edges (all of them, or just the ones solidworks_inspect listed with edges=true). Steps run in the given order on the active or named part. New parts are modelled freely; a part that already has a file needs allowEdit=true, and a backup copy of that file is written first. Changes stay in SolidWorks until solidworks_save is called. After modelling, check the attached screenshot and solidworks_inspect.",
      parameters: {
        type: "object",
        properties: {
          path: DOC_PATH,
          title: DOC_TITLE,
          allowEdit: {
            type: "boolean",
            description:
              "Required to modify a part that is already saved to disk: the original file is backed up first. New, not yet saved parts do not need it.",
          },
          preview: { type: "boolean", description: "Attach a screenshot of the result (default true)." },
          steps: {
            type: "array",
            minItems: 1,
            description:
              "Ordered modeling steps in millimetres. sketch: plane (front/top/right, a plane name, or a 1-based index) + optional offset (creates an offset plane) + shapes. extrude: depth, offset (start distance from the sketch plane), direction blind|midplane, flip. cut: throughAll (default unless depth is given), depth, offset, flip, bothDirections. revolve: angle (default 360), needs a centreline in the sketch. loft: profiles (two or more sketch names; defaults to the last two sketches). sweep: profile + path (defaults to the last sketch as profile and the one before as path). shell: thickness, faces top|bottom|front|back|left|right|none. surface: depth. fillet: radius, optional edgeIndices. chamfer: distance, optional edgeIndices. A cut, extrude or revolve uses the sketch from the previous sketch step unless sketch names one.",
            items: {
              type: "object",
              additionalProperties: true,
              properties: {
                op: {
                  type: "string",
                  enum: ["sketch", "extrude", "cut", "revolve", "loft", "sweep", "shell", "surface", "fillet", "chamfer"],
                },
                plane: {
                  type: "string",
                  description: "front | top | right, an existing plane name, or a 1-based plane index. Default: front.",
                },
                offset: {
                  type: "number",
                  description:
                    "sketch: distance in mm of a new offset plane to sketch on. extrude/cut: start distance in mm from the sketch plane.",
                },
                shapes: {
                  type: "array",
                  description: "Shapes drawn in the sketch, all in millimetres on the sketch plane.",
                  items: {
                    type: "object",
                    additionalProperties: true,
                    properties: {
                      type: { type: "string", enum: ["rectangle", "circle", "polygon", "polyline", "centerline"] },
                      center: {
                        type: "array",
                        items: { type: "number" },
                        description: "[x, y] centre in mm (default 0, 0).",
                      },
                      width: { type: "number", description: "rectangle width in mm." },
                      height: { type: "number", description: "rectangle height in mm." },
                      radius: { type: "number", description: "circle or polygon radius in mm." },
                      sides: { type: "integer", description: "polygon side count (3-64, default 6)." },
                      rotation: { type: "number", description: "polygon rotation in degrees." },
                      points: {
                        type: "array",
                        items: { type: "array", items: { type: "number" } },
                        description: "polyline [x, y] points in mm; a centerline needs exactly two points.",
                      },
                      closed: { type: "boolean", description: "close the polyline (default false)." },
                    },
                    required: ["type"],
                  },
                },
                sketch: { type: "string", description: "Name of an existing sketch to use." },
                depth: { type: "number", description: "extrude/cut/surface depth in mm." },
                throughAll: { type: "boolean", description: "cut through the whole part (default true)." },
                bothDirections: { type: "boolean", description: "cut in both directions." },
                flip: { type: "boolean", description: "reverse the extrude/cut/revolve direction." },
                direction: { type: "string", enum: ["blind", "midplane"], description: "extrude direction." },
                angle: { type: "number", description: "revolve angle in degrees (default 360)." },
                profiles: {
                  type: "array",
                  items: { type: "string" },
                  description: "loft: sketch names to loft through, in order.",
                },
                profile: { type: "string", description: "sweep: name of the profile sketch." },
                path: { type: "string", description: "sweep: name of the path sketch." },
                thickness: { type: "number", description: "shell wall thickness in mm (default 2)." },
                faces: {
                  type: "string",
                  enum: ["top", "bottom", "front", "back", "left", "right", "none"],
                  description: "shell: which face to leave open (default top); none makes a closed hollow body.",
                },
                outward: { type: "boolean", description: "shell outward instead of inward (default false)." },
                edgeIndices: {
                  type: "array",
                  items: { type: "integer" },
                  description:
                    "fillet/chamfer: 1-based edge numbers from solidworks_inspect edges=true. Omit to use every edge.",
                },
                radius: { type: "number", description: "fillet radius in mm." },
                distance: { type: "number", description: "chamfer distance in mm." },
              },
              required: ["op"],
            },
          },
        },
        required: ["steps"],
        additionalProperties: false,
      },
      output: withImage,
      timeoutMs: toolTimeout,
      async execute(args, exec) {
        const data = await call(
          {
            op: "model",
            width: settings.captureWidth,
            height: settings.captureHeight,
            ...args,
          },
          exec,
        );
        const images = [];
        if (data.previewPng) {
          try {
            const bytes = await readFile(data.previewPng);
            const attachment = await ctx.attachments.saveImage({
              data: bytes,
              mediaType: "image/png",
              name: basename(data.previewPng),
            });
            images.push(toJson(attachment));
          } finally {
            await rm(data.previewPng, { force: true }).catch(() => {});
          }
        }
        return { text: render.modeled(data), images };
      },
    },
    {
      name: "solidworks_assembly",
      description:
        "Insert parts or sub-assemblies into an open assembly, position them with plane-to-plane mates, and fix or float components. Steps run in order; componentA/componentB take the instance names this tool reports (for example \"Bracket-1\"), and planes are front/top/right or a plane name. A saved assembly needs allowEdit=true and is backed up first. Changes stay in SolidWorks until solidworks_save is called.",
      parameters: {
        type: "object",
        properties: {
          path: DOC_PATH,
          title: DOC_TITLE,
          allowEdit: {
            type: "boolean",
            description: "Required to change a saved assembly: the original file is backed up first.",
          },
          preview: { type: "boolean", description: "Attach a screenshot of the result (default true)." },
          steps: {
            type: "array",
            minItems: 1,
            description:
              "Ordered assembly steps. insert: path + optional x/y/z in mm and fix. mate: type + componentA/planeA and componentB/planeB, distance in mm for distance mates, angle in degrees for angle mates. fix / float: component.",
            items: {
              type: "object",
              additionalProperties: true,
              properties: {
                op: { type: "string", enum: ["insert", "mate", "fix", "float"] },
                path: { type: "string", description: "insert: absolute path of the part or assembly to insert." },
                configuration: { type: "string", description: "insert: configuration name to use." },
                x: { type: "number", description: "insert: x position in mm." },
                y: { type: "number", description: "insert: y position in mm." },
                z: { type: "number", description: "insert: z position in mm." },
                fix: { type: "boolean", description: "insert: fix the new component in place." },
                component: { type: "string", description: "fix/float: component instance name." },
                type: {
                  type: "string",
                  enum: ["coincident", "concentric", "perpendicular", "parallel", "tangent", "distance", "angle"],
                  description: "mate type (default coincident).",
                },
                componentA: { type: "string", description: "mate: first component instance name." },
                planeA: { type: "string", description: "mate: plane of the first component: front/top/right (bottom/back/left use the opposite plane with flipped alignment), or a plane name." },
                componentB: { type: "string", description: "mate: second component instance name." },
                planeB: { type: "string", description: "mate: plane of the second component (same names as planeA)." },
                distance: { type: "number", description: "distance mate value in mm." },
                angle: { type: "number", description: "angle mate value in degrees." },
                flip: { type: "boolean", description: "flip the mate alignment." },
              },
              required: ["op"],
            },
          },
        },
        required: ["steps"],
        additionalProperties: false,
      },
      output: withImage,
      timeoutMs: toolTimeout,
      async execute(args, exec) {
        const data = await call(
          { op: "assembly", width: settings.captureWidth, height: settings.captureHeight, ...args },
          exec,
        );
        const images = [];
        if (data.previewPng) {
          try {
            const bytes = await readFile(data.previewPng);
            const attachment = await ctx.attachments.saveImage({
              data: bytes,
              mediaType: "image/png",
              name: basename(data.previewPng),
            });
            images.push(toJson(attachment));
          } finally {
            await rm(data.previewPng, { force: true }).catch(() => {});
          }
        }
        return { text: render.assembly(data), images };
      },
    },
    {
      name: "solidworks_drawing",
      description:
        "Create a drawing from a part or assembly file on SolidWorks' default drawing template (standard views, optional model dimensions), or list the views of an open drawing. The new drawing is unsaved; save it with solidworks_save, then export it as PDF with solidworks_export.",
      parameters: {
        type: "object",
        properties: {
          action: { type: "string", enum: ["create", "views"], description: "Default: create." },
          path: DOC_PATH,
          title: DOC_TITLE,
          model: { type: "string", description: "create: absolute path of the part or assembly to draw." },
          template: {
            type: "string",
            description: "create: drawing template path; defaults to SolidWorks' default drawing template.",
          },
          views: {
            type: "array",
            items: { type: "string", enum: ["front", "top", "right", "left", "back", "bottom", "iso"] },
            description: "create: views to place (default front, top, right, iso).",
          },
          autoDimensions: { type: "boolean", description: "create: insert model dimensions (default true)." },
        },
        additionalProperties: false,
      },
      output: textOnly,
      timeoutMs: toolTimeout,
      async execute(args, exec) {
        const data = await call({ op: "drawing", ...args }, exec);
        return { text: render.drawing(data) };
      },
    },
    {
      name: "solidworks_export",
      description:
        "Export an open SolidWorks document to a file: pdf, step/stp, iges/igs, stl, x_t, x_b, 3mf for geometry, dxf/dwg for drawings, or png/jpg/tif for a picture of the current graphics view. Writes next to the document when out is omitted. Only exports a copy: the open document keeps its own file. Runs in the background with no window.",
      parameters: {
        type: "object",
        properties: {
          format: {
            type: "string",
            description:
              "Target format: pdf, step, stp, iges, igs, stl, dxf, dwg, x_t, x_b, 3mf, png, jpg, tif. Taken from out's extension when out is given and format is not. The out extension must name the format; a .SLDPRT/.SLDASM/.SLDDRW target is rejected.",
          },
          out: {
            type: "string",
            description: "Absolute output path, including the extension. Default: the document's own folder and name.",
          },
          path: DOC_PATH,
          title: DOC_TITLE,
          width: { type: "integer", description: "Pixel width for png/jpg/tif (default 1600)." },
          height: { type: "integer", description: "Pixel height for png/jpg/tif (default 1200)." },
          zoomToFit: { type: "boolean", description: "Zoom the view to fit before a raster export (default true)." },
        },
        additionalProperties: false,
      },
      output: textOnly,
      timeoutMs: toolTimeout,
      async execute(args, exec) {
        const data = await call({ op: "export", ...args }, exec);
        return { text: render.exported(data) };
      },
    },
    {
      name: "solidworks_capture",
      description:
        "Take a picture of the current SolidWorks graphics area and show it to you, so you can check what the model really looks like (shape, orientation, colours, whether a feature landed where it should). The picture is attached to the tool result; the user sees it too. Works in the default headless mode: SolidWorks renders off screen, so no window is needed.",
      parameters: {
        type: "object",
        properties: {
          path: DOC_PATH,
          title: DOC_TITLE,
          width: { type: "integer", description: "Pixel width (default from the plugin settings)." },
          height: { type: "integer", description: "Pixel height (default from the plugin settings)." },
          zoomToFit: { type: "boolean", description: "Zoom the view to fit the model first (default true)." },
        },
        additionalProperties: false,
      },
      output: withImage,
      timeoutMs: toolTimeout,
      async execute(args, exec) {
        const data = await call(
          {
            op: "capture",
            width: args.width ?? settings.captureWidth,
            height: args.height ?? settings.captureHeight,
            ...args,
          },
          exec,
        );
        try {
          const bytes = await readFile(data.png);
          const attachment = await ctx.attachments.saveImage({
            data: bytes,
            mediaType: "image/png",
            name: basename(data.png),
          });
          return { text: render.captured(data), images: [toJson(attachment)] };
        } finally {
          await rm(data.png, { force: true }).catch(() => {});
        }
      },
    },
    {
      name: "solidworks_save",
      description:
        "Save an open SolidWorks document, save it under another name, or close it (or close all documents). action=save only works once the document has a file: a brand-new document must be written with action=save_as and out. save_as writes .SLDPRT/.SLDASM/.SLDDRW matching the document type (a missing extension is added); use solidworks_export for pdf/step/stl and other formats. Closing refuses to throw away unsaved changes unless force=true is passed.",
      parameters: {
        type: "object",
        properties: {
          action: {
            type: "string",
            enum: ["save", "save_as", "close", "close_all"],
            description: "save = write the document in place; save_as = write to out; close / close_all = close windows.",
          },
          path: DOC_PATH,
          title: DOC_TITLE,
          out: {
            type: "string",
            description:
              "Target path for action=save_as. Must end in .SLDPRT, .SLDASM or .SLDDRW matching the document; a missing extension is added automatically. A relative path resolves against the document's folder, else Documents.",
          },
          force: {
            type: "boolean",
            description: "Discard unsaved changes when closing (default false: refuse and report).",
          },
        },
        required: ["action"],
        additionalProperties: false,
      },
      output: textOnly,
      timeoutMs: toolTimeout,
      async execute(args, exec) {
        const data = await call({ op: "save", ...args }, exec);
        return { text: render.saved(data) };
      },
    },
  ];

  for (const tool of tools) {
    ctx.effect(() => ctx.tools.register(tool), `solidworks: tool ${tool.name}`);
  }
}


