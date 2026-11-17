// Tiny DOM helpers: h() and keyed list reconciliation. No virtual DOM.

const BOOL_PROPS = new Set(['disabled', 'checked', 'hidden', 'inert', 'required', 'readOnly', 'selected', 'multiple', 'autofocus', 'open']);

/**
 * h('button', { class: 'ts-button', onClick, dataset: { action: 'stop' }, 'aria-label': '…' }, 'Закончил')
 * Props: class / className (string or array), style (string or object; '--var' keys allowed), dataset, on<Event>
 * listeners (onClick → 'click'; pass [fn, options] for options), boolean props, any other attribute.
 * Children: nodes, strings, numbers, arrays; null / false / undefined are skipped.
 */
export function h(tag, props, ...children) {
  const el = document.createElement(tag);
  if (props) setProps(el, props);
  append(el, children);
  return el;
}

export function setProps(el, props) {
  for (const [k, v] of Object.entries(props)) {
    if (v == null || v === false && !BOOL_PROPS.has(k) && !k.startsWith('aria-')) continue;
    if (k === 'class' || k === 'className') el.className = Array.isArray(v) ? v.filter(Boolean).join(' ') : v;
    else if (k === 'style') {
      if (typeof v === 'string') el.style.cssText = v;
      else for (const [sk, sv] of Object.entries(v)) {
        if (sv == null) continue;
        if (sk.startsWith('--')) el.style.setProperty(sk, String(sv));
        else el.style[sk] = sv;
      }
    } else if (k === 'dataset') {
      for (const [dk, dv] of Object.entries(v)) if (dv != null) el.dataset[dk] = String(dv);
    } else if (k.startsWith('on') && (typeof v === 'function' || Array.isArray(v))) {
      const [fn, opts] = Array.isArray(v) ? v : [v, undefined];
      el.addEventListener(k.slice(2).toLowerCase(), fn, opts);
    } else if (k === 'ref' && typeof v === 'function') v(el);
    else if (BOOL_PROPS.has(k)) el[k] = !!v;
    else if (k === 'value' || k === 'htmlFor' || k === 'textContent') el[k] = v;
    else el.setAttribute(k, v === true ? '' : String(v));
  }
  return el;
}

export function append(el, children) {
  for (const c of children) {
    if (c == null || c === false || c === true) continue;
    if (Array.isArray(c)) append(el, c);
    else el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return el;
}

/** Replace all children. */
export function replaceChildren(el, ...children) {
  el.replaceChildren();
  return append(el, children);
}

/** Set text only when it changed (avoids needless layout). */
export function setText(el, text) {
  const t = String(text ?? '');
  if (el.textContent !== t) el.textContent = t;
}

/** Toggle / set a data attribute only when it changed. null removes it. */
export function setData(el, name, value) {
  const v = value == null || value === false ? null : String(value);
  if (v == null) { if (name in el.dataset) delete el.dataset[name]; }
  else if (el.dataset[name] !== v) el.dataset[name] = v;
}

let uidCounter = 0;
export const uid = (prefix = 'ts') => `${prefix}-${++uidCounter}`;

/**
 * Keyed reconciliation. Keeps one DOM node per key between renders, so designs can animate moves and changes.
 * @param {Element} parent
 * @param {Array} items
 * @param {{ key(item): string, create(item): Element, update?(el, item, index), remove?(el, done) }} spec
 *   remove(el, done) may run an exit animation and call done() to detach; otherwise the node is removed at once.
 */
export function reconcile(parent, items, spec) {
  const existing = new Map();
  for (const child of [...parent.children]) {
    if (child.__tsKey != null && !child.__tsLeaving) existing.set(child.__tsKey, child);
  }
  const used = new Set();
  let cursor = parent.firstElementChild;
  items.forEach((item, i) => {
    const key = spec.key(item);
    let el = existing.get(key);
    if (el && !used.has(key)) {
      spec.update?.(el, item, i);
    } else {
      el = spec.create(item, i);
      el.__tsKey = key;
      spec.update?.(el, item, i);
    }
    used.add(key);
    while (cursor && cursor.__tsLeaving) cursor = cursor.nextElementSibling;
    if (el !== cursor) parent.insertBefore(el, cursor);
    else cursor = cursor.nextElementSibling;
  });
  for (const [key, el] of existing) {
    if (used.has(key)) continue;
    if (spec.remove) {
      el.__tsLeaving = true;
      spec.remove(el, () => el.remove());
    } else el.remove();
  }
}

/** Focusable descendants (visible, enabled, not inert). */
export function focusables(root) {
  return [...root.querySelectorAll('a[href],button,input,select,textarea,[tabindex],summary,[contenteditable="true"]')]
    .filter((el) => !el.disabled && el.tabIndex >= 0 && !el.closest('[inert],[hidden]') && el.getClientRects().length);
}

/** True when the user is typing in a field. */
export function isTyping() {
  const a = document.activeElement;
  return !!a && (a.tagName === 'TEXTAREA' || (a.tagName === 'INPUT' && !['button', 'checkbox', 'radio', 'submit', 'range'].includes(a.type)) || a.isContentEditable);
}

export const prefersReducedMotion = () => matchMedia('(prefers-reduced-motion: reduce)').matches;

/** Run fn on the next animation frame, once per frame per key. */
const rafKeys = new Map();
export function rafOnce(key, fn) {
  if (rafKeys.has(key)) { rafKeys.set(key, fn); return; }
  rafKeys.set(key, fn);
  requestAnimationFrame(() => { const f = rafKeys.get(key); rafKeys.delete(key); f(); });
}
