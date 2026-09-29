const $ = id => document.getElementById(id);
const OPS = [
    ['=', 'igual a'], ['<>', 'diferente de'], ['>', 'maior que'], ['<', 'menor que'],
    ['>=', 'maior ou igual a'], ['<=', 'menor ou igual a'], ['CONTAINS', 'contém'],
    ['LIKE', 'LIKE (use %)'], ['NOT LIKE', 'NOT LIKE'], ['IS NULL', 'é nulo'], ['IS NOT NULL', 'não é nulo']
];
let columns = [];

async function api(url, opts) {
    const res = await fetch(url, opts);
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(data.error || 'Erro ' + res.status);
    return data;
}

function opt(value, text) {
    const o = document.createElement('option');
    o.value = value; o.textContent = text ?? value;
    return o;
}

function colSelect() {
    const s = document.createElement('select');
    columns.forEach(c => s.append(opt(c)));
    return s;
}

function removeBtn(row) {
    const b = document.createElement('button');
    b.type = 'button'; b.textContent = '✕'; b.className = 'x';
    b.onclick = () => row.remove();
    return b;
}

async function loadColumns() {
    const t = encodeURIComponent($('table').value);
    columns = (await api(`/api/tables/${t}/columns`)).map(c => c.name);
    $('cols').replaceChildren(); $('filters').replaceChildren(); $('sorts').replaceChildren();
    columns.forEach(name => {
        const l = document.createElement('label');
        const cb = document.createElement('input');
        cb.type = 'checkbox'; cb.value = name;
        cb.onchange = () => { if (cb.checked) $('allCols').checked = false; };
        l.append(cb, ' ', name);
        $('cols').append(l);
    });
    $('allCols').checked = true;
}

$('allCols').onchange = () => {
    if ($('allCols').checked) $('cols').querySelectorAll('input').forEach(i => i.checked = false);
};

function addFilter() {
    const row = document.createElement('div'); row.className = 'row';
    const col = colSelect();
    const op = document.createElement('select');
    OPS.forEach(([v, t]) => op.append(opt(v, t)));
    const val = document.createElement('input'); val.placeholder = 'valor';
    op.onchange = () => {
        const isNull = op.value.startsWith('IS');
        val.disabled = isNull; if (isNull) val.value = '';
    };
    row.append(col, op, val, removeBtn(row));
    $('filters').append(row);
}

function addSort() {
    const row = document.createElement('div'); row.className = 'row';
    const dir = document.createElement('select');
    dir.append(opt('ASC', 'crescente (A→Z, 0→9)'), opt('DESC', 'decrescente (Z→A, 9→0)'));
    row.append(colSelect(), dir, removeBtn(row));
    $('sorts').append(row);
}

function buildRequest() {
    const selected = $('allCols').checked
        ? [] : [...$('cols').querySelectorAll('input:checked')].map(i => i.value);
    const filters = [...$('filters').children].map(r => {
        const [c, o, v] = r.querySelectorAll('select, input');
        return { column: c.value, op: o.value, value: v.value };
    });
    const sort = [...$('sorts').children].map(r => {
        const [c, d] = r.querySelectorAll('select');
        return { column: c.value, direction: d.value };
    });
    return {
        table: $('table').value, columns: selected, filters,
        logic: $('logic').value, sort
    };
}

async function send() {
    $('error').textContent = '';
    try {
        const data = await api('/api/query/generate', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(buildRequest())
        });
        $('sql').textContent = data.sql;
        $('out').hidden = false;
    } catch (e) {
        $('error').textContent = e.message;
    }
}

$('table').onchange = loadColumns;
$('addFilter').onclick = addFilter;
$('addSort').onclick = addSort;
$('btnGen').onclick = send;
$('btnCopy').onclick = async () => {
    await navigator.clipboard.writeText($('sql').textContent);
    $('btnCopy').textContent = 'Copiado!';
    setTimeout(() => $('btnCopy').textContent = 'Copiar query', 1500);
};

(async function init() {
    try {
        const tables = await api('/api/tables');
        tables.forEach(t => $('table').append(opt(t)));
        await loadColumns();
    } catch (e) { $('error').textContent = 'Não foi possível carregar o banco: ' + e.message; }
})();