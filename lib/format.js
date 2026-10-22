/** Human-readable renderings of the driver's JSON answers. Units: SolidWorks reports metres and kilograms. */

function num(value) {
  if (typeof value !== "number" || !Number.isFinite(value)) return "?";
  if (value === 0) return "0";
  const absolute = Math.abs(value);
  if (absolute >= 1e6 || absolute < 1e-4) return value.toExponential(4);
  return String(Number(value.toPrecision(6)));
}

function fixed(value, digits) {
  if (typeof value !== "number" || !Number.isFinite(value)) return "?";
  return num(Number(value.toFixed(digits)));
}

const mm = (metres) => (typeof metres === "number" ? num(Number((metres * 1000).toFixed(3))) : "?");
const mm3 = (metres3) => (typeof metres3 === "number" ? num(Number((metres3 * 1e6).toFixed(3))) : "?");
const mm2 = (metres2) => (typeof metres2 === "number" ? num(Number((metres2 * 1e6).toFixed(2))) : "?");
const kg = (kilograms) => (typeof kilograms === "number" ? num(Number(kilograms.toFixed(6))) : "?");
const kB = (bytes) => {
  if (typeof bytes !== "number") return "?";
  if (bytes >= 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} kB`;
  return `${bytes} B`;
};

const TYPE_NAMES = { part: "part", assembly: "assembly", drawing: "drawing" };

function docHeading(doc) {
  if (!doc) return "No document.";
  const flags = [doc.type, doc.active ? "active" : null, doc.modified ? "unsaved changes" : null, doc.readOnly ? "read only" : null]
    .filter(Boolean)
    .join(", ");
  const where = doc.path ? doc.path : "not saved yet";
  return `${doc.title}  —  ${flags}\n  ${where}`;
}

function docLine(doc) {
  const flags = [
    doc.type,
    doc.active ? "active" : null,
    doc.modified ? "unsaved" : null,
    doc.readOnly ? "read only" : null,
  ]
    .filter(Boolean)
    .join(", ");
  const where = doc.path ? `  ${doc.path}` : "  (not saved yet)";
  return `  - ${doc.title}  [${flags}]${where}`;
}

export function status(data) {
  const lines = [];
  if (!data.installed) {
    lines.push("SolidWorks is not installed on this computer.");
    lines.push("Install SOLIDWORKS (2014 or newer) and restart this chat session; the tools need its COM automation API.");
    return lines.join("\n");
  }
  if (!data.running) {
    lines.push(`SolidWorks ${data.exeVersion || ""} is installed but not running.`.replace("  ", " "));
    lines.push(`  ${data.exePath}`);
    lines.push("The next tool call that needs SolidWorks starts it (about 30 seconds on the first call).");
    return lines.join("\n");
  }
  lines.push(`SolidWorks ${data.version || data.exeVersion || ""} is running${data.visible ? "" : " (window hidden)"}.`);
  lines.push(`  ${data.exePath}`);
  if (data.attached) {
    const documents = data.documents ?? [];
    lines.push("");
    lines.push(documents.length === 0 ? "No document is open." : `Open documents (${documents.length}):`);
    for (const doc of documents) lines.push(docLine(doc));
    if (data.active) lines.push("", "Active document:", docHeading(data.active));
    if (data.templates?.part) {
      lines.push("", `Default templates: part ${data.templates.part}`);
      if (data.templates.assembly) lines.push(`  assembly ${data.templates.assembly}`);
      if (data.templates.drawing) lines.push(`  drawing ${data.templates.drawing}`);
    }
  } else {
    lines.push("Open documents were not read (automation was not attached).");
  }
  return lines.join("\n");
}

export function documents(data) {
  const list = data.documents ?? [];
  if (list.length === 0) return "No document is open in SolidWorks.";
  const lines = [`Open documents (${list.length}):`];
  for (const doc of list) lines.push(docLine(doc));
  if (data.active) lines.push("", "Active document:", docHeading(data.active));
  return lines.join("\n");
}

export function opened(data) {
  const doc = data.doc;
  const verb = data.created ? "Created a new document" : "Opened";
  return `${verb}: ${doc.title}  [${doc.type}]${doc.path ? `\n  ${doc.path}` : "\n  (not saved yet)"}`;
}

export function inspect(data) {
  const doc = data.doc;
  const lines = [];
  lines.push(docHeading(doc));
  lines.push("");

  const configurations = data.configurations ?? [];
  if (configurations.length > 0) {
    const active = configurations.find((item) => item.active)?.name;
    lines.push(
      `Configurations: ${configurations.map((item) => (item.active ? `${item.name}*` : item.name)).join(", ")}` +
        (active ? `  (${configurations.length} total, * = active)` : ""),
    );
  }

  if (doc.type === "part") {
    lines.push(`Material: ${data.material ? data.material : "not assigned"}`);
    const mass = data.mass;
    if (mass && typeof mass.mass === "number" && (mass.mass > 0 || mass.volume > 0)) {
      lines.push(
        `Mass: ${kg(mass.mass)} kg   density: ${num(mass.density)} kg/m³   volume: ${mm3(mass.volume)} cm³   surface: ${mm2(mass.area)} mm²`,
      );
      if (Array.isArray(mass.centerOfMass)) {
        const [x, y, z] = mass.centerOfMass;
        lines.push(`Center of mass: (${mm(x)}, ${mm(y)}, ${mm(z)}) mm`);
      }
    } else {
      lines.push("Mass: not available (no solid body, or no material assigned).");
    }
    if (data.boundingBox) {
      const { min, max, size } = data.boundingBox;
      lines.push(
        `Bounding box: ${mm(size[0])} × ${mm(size[1])} × ${mm(size[2])} mm  ` +
          `(x ${mm(min[0])}..${mm(max[0])}, y ${mm(min[1])}..${mm(max[1])}, z ${mm(min[2])}..${mm(max[2])})`,
      );
    }
    lines.push(`Solid bodies: ${data.bodyCount ?? 0}` + (data.sheetBodyCount > 0 ? `   surface bodies: ${data.sheetBodyCount}` : ""));
  }

  const edges = data.edges ?? [];
  if (edges.length > 0) {
    lines.push("");
    lines.push(`Edges (${edges.length}; pass these numbers as edgeIndices to a fillet or chamfer step):`);
    for (const edge of edges.slice(0, 100)) {
      const from = Array.isArray(edge.start) ? edge.start.map((value) => num(value)).join(", ") : "?";
      const to = Array.isArray(edge.end) ? edge.end.map((value) => num(value)).join(", ") : "?";
      const chord = typeof edge.chord === "number" ? `  chord ${num(edge.chord)} mm` : "";
      lines.push(`  ${edge.index}. (${from}) → (${to})${chord}`);
    }
    if (edges.length > 100) lines.push(`  … ${edges.length - 100} more`);
  }

  const features = data.features ?? [];
  if (features.length > 0) {
    const shown = features.slice(0, 80);
    lines.push("");
    lines.push(`Features (${features.length}):`);
    lines.push(`  ${shown.map((feature) => `${feature.name} (${feature.type})`).join(", ")}`);
    if (features.length > shown.length) lines.push(`  … ${features.length - shown.length} more`);
  }

  if (doc.type === "assembly") {
    const components = data.components ?? [];
    lines.push("");
    lines.push(`Components (${data.componentCount ?? components.length} top-level):`);
    for (const component of components.slice(0, 80)) {
      const flags = [component.configuration ? `config ${component.configuration}` : null, component.suppressed ? "suppressed" : null]
        .filter(Boolean)
        .join(", ");
      lines.push(`  - ${component.name}${flags ? `  [${flags}]` : ""}${component.path ? `  ${component.path}` : ""}`);
    }
    if (components.length > 80) lines.push(`  … ${components.length - 80} more`);
  }

  if (doc.type === "drawing") {
    const sheets = data.sheets ?? [];
    lines.push("");
    lines.push(`Sheets (${sheets.length}): ${sheets.join(", ")}${data.currentSheet ? `; current ${data.currentSheet}` : ""}`);
    lines.push(`Views: ${data.viewCount ?? 0}`);
  }

  const properties = data.customProperties ?? {};
  const names = Object.keys(properties);
  const filled = names.filter((name) => String(properties[name] ?? "").trim() !== "");
  lines.push("");
  if (names.length === 0) {
    lines.push("Custom properties: none.");
  } else if (filled.length === 0) {
    lines.push(`Custom properties: ${names.length}, all empty.`);
  } else {
    lines.push(`Custom properties (${names.length}${names.length > filled.length ? `, ${filled.length} with a value` : ""}):`);
    for (const name of filled.slice(0, 60)) {
      lines.push(`  ${name} = ${String(properties[name]).trim()}`);
    }
    if (filled.length > 60) lines.push(`  … ${filled.length - 60} more`);
  }
  return lines.join("\n");
}

export function properties(data) {
  const doc = data.doc;
  const scope = data.configuration ? `configuration "${data.configuration}"` : "document level";
  const entries = Object.entries(data.properties ?? {});
  const lines = [];
  if (data.changed > 0) {
    lines.push(`Updated ${data.changed} ${data.changed === 1 ? "property" : "properties"}.`, "");
  }
  lines.push(`${doc.title} — custom properties, ${scope} (${entries.length}):`);
  if (entries.length === 0) lines.push("  (none)");
  for (const [name, value] of entries.slice(0, 80)) {
    const text = String(value ?? "").trim();
    lines.push(`  ${name} = ${text === "" ? "(empty)" : text}`);
  }
  if (entries.length > 80) lines.push(`  … ${entries.length - 80} more`);
  if (data.changed > 0) {
    lines.push("", "These changes are in SolidWorks' memory only; solidworks_save with action=save writes them into the file.");
  }
  return lines.join("\n");
}

export function exported(data) {
  return (
    `Exported ${data.doc.title} as ${String(data.format).toUpperCase()}:\n` +
    `  ${data.out}  (${kB(data.bytes)})`
  );
}

export function captured(data) {
  return `Current SolidWorks graphics view of ${data.doc.title} (${data.width}×${data.height} pixels).`;
}

export function modeled(data) {
  const doc = data.doc;
  const lines = [];
  lines.push(`Modeled ${doc.title}  [${doc.type}]${doc.path ? `\n  ${doc.path}` : "\n  (not saved yet)"}`);
  lines.push("");
  lines.push(`Steps (${data.steps.length}):`);
  for (const step of data.steps) lines.push(`  ${step.step}. ${step.op} → ${step.feature || "(no feature)"}`);

  const mass = data.mass;
  if (mass && typeof mass.volume === "number" && (mass.volume > 0 || mass.mass > 0)) {
    lines.push("");
    lines.push(
      `Volume: ${mm3(mass.volume)} cm³   mass: ${kg(mass.mass)} kg   surface: ${mm2(mass.area)} mm²`,
    );
  }
  lines.push(`Solid bodies: ${data.bodyCount ?? 0}` + (data.sheetBodyCount > 0 ? `   surface bodies: ${data.sheetBodyCount}` : ""));
  if (Array.isArray(data.features) && data.features.length > 0) {
    lines.push(`Feature tree: ${data.features.map((f) => f.name).join(" → ")}`);
  }
  if (data.backup) {
    lines.push("");
    lines.push(`Backup of the previous file: ${data.backup}`);
  }
  for (const note of data.notes ?? []) {
    if (!note.startsWith("Backup of the original file")) lines.push(note);
  }
  lines.push("");
  lines.push(
    doc.path
      ? "Changes are in SolidWorks only; solidworks_save with action=save writes them into the file."
      : "This part has no file yet, so action=save cannot work: call solidworks_save with action=save_as and out=\"<folder>\\<name>.SLDPRT\".",
  );
  return lines.join("\n");
}

export function saved(data) {
  switch (data.action) {
    case "save":
      return `Saved ${data.doc.title}${data.path ? `\n  ${data.path}` : ""}`;
    case "save_as":
      return `Saved ${data.doc.title} as\n  ${data.path}`;
    case "close":
      return `Closed ${data.title}.`;
    case "close_all":
      return data.closed?.length ? `Closed ${data.closed.length} document(s): ${data.closed.join(", ")}.` : "No document was open.";
    default:
      return JSON.stringify(data);
  }
}



export function assembly(data) {
  const doc = data.doc;
  const lines = [];
  lines.push(`Assembly ${doc.title}${doc.path ? `\n  ${doc.path}` : "\n  (not saved yet)"}`);
  lines.push("");
  lines.push(`Steps (${data.steps.length}):`);
  for (const step of data.steps) lines.push(`  ${step.step}. ${step.op} → ${step.feature || "(done)"}`);

  const components = data.components ?? [];
  lines.push("");
  lines.push(`Components (${data.componentCount ?? components.length}):`);
  if (components.length === 0) lines.push("  (none)");
  for (const component of components) {
    const flags = [
      component.fixed ? "fixed" : "floating",
      component.suppressed ? "suppressed" : null,
      component.configuration ? `config ${component.configuration}` : null,
    ]
      .filter(Boolean)
      .join(", ");
    lines.push(`  - ${component.name}  [${flags}]${component.path ? `\n    ${component.path}` : ""}`);
  }

  const mass = data.mass;
  if (mass && typeof mass.volume === "number" && (mass.volume > 0 || mass.mass > 0)) {
    lines.push("");
    lines.push(`Volume: ${mm3(mass.volume)} cm³   mass: ${kg(mass.mass)} kg`);
  }
  if (data.backup) lines.push("", `Backup of the previous file: ${data.backup}`);
  for (const note of data.notes ?? []) {
    if (!note.startsWith("Backup of the original file")) lines.push(note);
  }
  lines.push("");
  lines.push(
    doc.path
      ? "Changes are in SolidWorks only; solidworks_save with action=save writes them into the file."
      : "This assembly has no file yet, so action=save cannot work: call solidworks_save with action=save_as and out=\"<folder>\\<name>.SLDASM\".",
  );
  return lines.join("\n");
}

export function drawing(data) {
  const doc = data.doc;
  const views = data.views ?? [];
  const lines = [];
  lines.push(`Drawing ${doc.title}${doc.path ? `\n  ${doc.path}` : "\n  (not saved yet)"}`);
  if (data.sheet) lines.push(`Sheet: ${data.sheet}`);
  lines.push("");
  lines.push(`Views (${views.length}):`);
  if (views.length === 0) lines.push("  (none)");
  for (const view of views) {
    const label = view.view ? `  (${view.view})` : view.type !== undefined ? `  type ${view.type}` : "";
    lines.push(`  - ${view.name}${label}`);
  }
  for (const note of data.notes ?? []) lines.push("", note);
  const created = views.some((view) => view.view !== undefined);
  lines.push("");
  if (!created) lines.push("This only lists the views; nothing was changed.");
  else if (doc.path) lines.push("Changes are in SolidWorks only; solidworks_save with action=save writes them into the file.");
  else lines.push("This drawing has no file yet, so action=save cannot work: call solidworks_save with action=save_as and out=\"<folder>\\<name>.SLDDRW\".");
  return lines.join("\n");
}




