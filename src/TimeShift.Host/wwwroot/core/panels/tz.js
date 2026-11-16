// Time zone picker: searchable list from Intl.supportedValuesOf('timeZone').
import { h } from '../dom.js';
import * as T from '../time.js';

/**
 * <div class="ts-tz-picker"><input class="ts-input ts-search"><ul class="ts-tz-list"><li><button class="ts-tz-option" aria-selected>
 * @param {string} current
 * @param {(tz: string) => void} onPick
 */
export function tzPicker(current, onPick, { limit = 80 } = {}) {
  const all = T.timeZones().map((tz) => ({ tz, label: T.fmtTimeZone(tz) }));
  const search = h('input', { type: 'search', class: 'ts-input ts-search', placeholder: 'Город или пояс', autocomplete: 'off', enterkeyhint: 'search', 'aria-label': 'Поиск часового пояса' });
  const list = h('ul', { class: 'ts-tz-list', role: 'listbox', 'aria-label': 'Часовые пояса' });
  const empty = h('p', { class: 'ts-empty', hidden: true }, 'Ничего не нашлось.');
  let selected = current;
  const render = () => {
    const q = search.value.trim().toLowerCase();
    const found = (q ? all.filter((z) => z.tz.toLowerCase().includes(q) || z.label.toLowerCase().includes(q)) : all)
      .sort((a, b) => (a.tz === selected ? -1 : b.tz === selected ? 1 : 0))
      .slice(0, limit);
    list.replaceChildren(...found.map((z) => h('li', { role: 'option', 'aria-selected': String(z.tz === selected), class: 'ts-tz-item' },
      h('button', { type: 'button', class: 'ts-tz-option', dataset: { tz: z.tz }, 'aria-pressed': String(z.tz === selected), onClick: () => { selected = z.tz; render(); onPick(z.tz); } },
        h('span', { class: 'ts-tz-label' }, z.label), h('span', { class: 'ts-tz-id' }, z.tz)))));
    empty.hidden = found.length > 0;
  };
  search.addEventListener('input', render);
  render();
  return { el: h('div', { class: 'ts-tz-picker' }, search, list, empty), search, set(tz) { selected = tz; render(); } };
}
