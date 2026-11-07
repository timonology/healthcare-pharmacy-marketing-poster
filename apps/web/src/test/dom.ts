/**
 * A deliberately small DOM for component tests, installed on import.
 *
 * Just what react-dom 18 touches to mount, commit, call ref callbacks, run
 * effects and re-render — plus the handful of element APIs the components under
 * test use (pointer capture, bounding boxes, native listeners, image size). It
 * is not a browser: layout is whatever a test says it is.
 *
 * Images report the size registered for their `src` in `imageSizes`, and are
 * already `complete` when created — which is exactly what a local blob URL for
 * a just-attached photo is, and exactly the case that crashed the framer.
 */

type Listener = (event: unknown) => void;

export class FakeNode {
  nodeType: number;
  nodeName: string;
  tagName: string;
  childNodes: FakeNode[] = [];
  parentNode: FakeNode | null = null;
  attributes: Record<string, string> = {};
  style: Record<string, string> = {};
  listeners: Record<string, Listener[]> = {};
  namespaceURI = "http://www.w3.org/1999/xhtml";
  box = { left: 0, top: 0, width: 108, height: 192 };

  constructor(nodeType: number, nodeName: string) {
    this.nodeType = nodeType;
    this.nodeName = nodeName;
    this.tagName = nodeName;
  }

  get ownerDocument() {
    return (globalThis as unknown as { document: unknown }).document;
  }
  get firstChild() {
    return this.childNodes[0] ?? null;
  }
  get lastChild() {
    return this.childNodes[this.childNodes.length - 1] ?? null;
  }
  get nextSibling() {
    const siblings = this.parentNode?.childNodes ?? [];
    return siblings[siblings.indexOf(this) + 1] ?? null;
  }
  appendChild(child: FakeNode) {
    child.parentNode?.removeChild(child);
    child.parentNode = this;
    this.childNodes.push(child);
    return child;
  }
  insertBefore(child: FakeNode, ref: FakeNode | null) {
    if (!ref) return this.appendChild(child);
    child.parentNode?.removeChild(child);
    child.parentNode = this;
    this.childNodes.splice(this.childNodes.indexOf(ref), 0, child);
    return child;
  }
  removeChild(child: FakeNode) {
    const i = this.childNodes.indexOf(child);
    if (i >= 0) this.childNodes.splice(i, 1);
    child.parentNode = null;
    return child;
  }
  setAttribute(name: string, value: unknown) {
    this.attributes[name] = String(value);
  }
  removeAttribute(name: string) {
    delete this.attributes[name];
  }
  getAttribute(name: string) {
    return this.attributes[name] ?? null;
  }
  hasAttribute(name: string) {
    return name in this.attributes;
  }
  addEventListener(type: string, fn: Listener) {
    (this.listeners[type] ??= []).push(fn);
  }
  removeEventListener(type: string, fn: Listener) {
    this.listeners[type] = (this.listeners[type] ?? []).filter((f) => f !== fn);
  }
  get textContent(): string {
    return this.childNodes.map((c) => c.textContent).join("");
  }
  set textContent(value: string) {
    this.childNodes = [];
    if (value) this.appendChild(new FakeText(value));
  }
  getBoundingClientRect() {
    return this.box;
  }
  captured = new Set<number>();
  setPointerCapture(id: number) {
    this.captured.add(id);
  }
  releasePointerCapture(id: number) {
    if (!this.captured.has(id)) throw new Error("released a capture that was never taken");
    this.captured.delete(id);
  }
  hasPointerCapture(id: number) {
    return this.captured.has(id);
  }
}

class FakeText extends FakeNode {
  data: string;
  constructor(text: string) {
    super(3, "#text");
    this.data = text;
  }
  get textContent() {
    return this.data;
  }
  set textContent(value: string) {
    this.data = value;
  }
  get nodeValue() {
    return this.data;
  }
  set nodeValue(value: string) {
    this.data = value;
  }
}

/** `src` → natural size, for images created after registration. */
export const imageSizes = new Map<string, [number, number]>();

class FakeImage extends FakeNode {
  complete = true;
  constructor() {
    super(1, "IMG");
  }
  get naturalWidth() {
    return imageSizes.get(this.attributes.src ?? "")?.[0] ?? 0;
  }
  get naturalHeight() {
    return imageSizes.get(this.attributes.src ?? "")?.[1] ?? 0;
  }
}

/**
 * A `<video>` with the one behaviour that matters to the kiosk: a `play()`
 * promise that stays pending until playback starts, is rejected with
 * AbortError by `pause()` or `load()` (as browsers do), and is rejected with
 * NotSupportedError when there is no source.
 */
export class FakeVideo extends FakeNode {
  muted = false;
  currentTime = 0;
  paused = true;
  plays = 0;
  private pending: { resolve: () => void; reject: (e: Error) => void } | null = null;

  constructor() {
    super(1, "VIDEO");
  }
  get src() {
    return this.attributes.src ?? "";
  }
  set src(value: string) {
    this.attributes.src = String(value);
  }
  play(): Promise<void> {
    this.plays++;
    if (!this.attributes.src) {
      return Promise.reject(Object.assign(new Error("no supported source"), { name: "NotSupportedError" }));
    }
    this.paused = false;
    return new Promise<void>((resolve, reject) => {
      this.pending = { resolve, reject };
    });
  }
  /** The stream delivered its first frame. */
  startPlaying() {
    this.currentTime = 0.1;
    this.pending?.resolve();
    this.pending = null;
  }
  private abort() {
    this.pending?.reject(Object.assign(new Error("interrupted"), { name: "AbortError" }));
    this.pending = null;
  }
  pause() {
    this.paused = true;
    this.abort();
  }
  load() {
    this.abort();
    this.currentTime = 0;
  }
}

const doc = {
  nodeType: 9,
  createElement: (tag: string) =>
    tag.toLowerCase() === "img" ? new FakeImage()
    : tag.toLowerCase() === "video" ? new FakeVideo()
    : new FakeNode(1, tag.toUpperCase()),
  createElementNS: (_ns: string, tag: string) => new FakeNode(1, tag.toUpperCase()),
  createTextNode: (text: string) => new FakeText(text),
  createComment: (text: string) => new FakeText(text),
  addEventListener() {},
  removeEventListener() {},
  documentElement: new FakeNode(1, "HTML"),
  body: new FakeNode(1, "BODY"),
  activeElement: null,
};

const g = globalThis as unknown as Record<string, unknown>;
g.document = doc;
g.window = globalThis;
g.HTMLElement = FakeNode;
g.HTMLIFrameElement = class {};
g.Node = FakeNode;
g.IS_REACT_ACT_ENVIRONMENT = true;
if (!("navigator" in globalThis)) g.navigator = { userAgent: "node" };

// ── helpers for tests ───────────────────────────────────────────────────

/** Every node under `root` matching `test`, depth first. */
export function findAll(root: FakeNode, test: (n: FakeNode) => boolean): FakeNode[] {
  const out: FakeNode[] = [];
  const walk = (n: FakeNode) => {
    if (test(n)) out.push(n);
    n.childNodes.forEach(walk);
  };
  walk(root);
  return out;
}

/** The props React last rendered onto a host element — to call its handlers. */
export function reactProps(el: FakeNode): Record<string, (...args: unknown[]) => unknown> {
  const key = Object.keys(el).find((k) => k.startsWith("__reactProps"));
  if (!key) throw new Error(`${el.nodeName} has no React props`);
  return (el as unknown as Record<string, Record<string, (...args: unknown[]) => unknown>>)[key];
}

export function newContainer(): FakeNode {
  const container = doc.createElement("div") as FakeNode;
  doc.body.appendChild(container);
  return container;
}
