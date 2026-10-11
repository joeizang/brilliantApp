// Lets a finger (or pen) drag a line of a Parsons step by its grip. The browser's own drag-and-drop doesn't start from touch in
// a WebView, so this follows the pointer itself and, when it lets go, tells the step to move the line (ParsonsStepView.MoveTo).
// Mouse pointers are left to the native drag, which already works.

const EDGE = 48;   // px from the top or bottom of the window where dragging scrolls the page
const STEP = 14;

/** The row a pointer at `y` is over: the first row whose bottom is below it, else the last. `rects` are the rows' bounding boxes, top to bottom. */
export function dropIndex(rects, y) {
    for (let i = 0; i < rects.length; i++) if (y < rects[i].bottom) return i;
    return rects.length - 1;
}

/** The rows' boxes as they sit when nothing is being dragged: the dragged row (`dragged`) has been moved `offsetY` by the transform that follows the finger, so it is moved back. */
export function restingRects(rects, dragged, offsetY) {
    return rects.map((r, i) => i === dragged ? { top: r.top - offsetY, bottom: r.bottom - offsetY } : r);
}

/** How far to scroll for a pointer at `y` in a window `height` tall: up (negative) near the top, down near the bottom, else none. */
export function scrollDelta(y, height) {
    if (y < EDGE) return -STEP;
    if (y > height - EDGE) return STEP;
    return 0;
}

const active = new WeakMap();

/** Starts listening on `list` (the <ol> of rows). Rows carry data-row (their index) and the grips are `.grip`; `ref.MoveTo(from, to)` is called on release. */
export function attach(list, ref) {
    detach(list);
    let pointerType = 'mouse';
    let drag = null;

    const rows = () => [...list.querySelectorAll(':scope > li')];

    // The native drag must not also start from a finger or pen.
    const onPointerDownCapture = e => { pointerType = e.pointerType; };
    const onDragStart = e => {
        if (pointerType === 'mouse') return;
        e.preventDefault();
        e.stopPropagation();
    };

    const onDown = e => {
        const grip = e.target.closest?.('.grip');
        if (!grip || e.pointerType === 'mouse' || list.dataset.locked === 'true' || drag) return;
        const row = grip.closest('li');
        const from = Number(row.dataset.row);
        grip.setPointerCapture(e.pointerId);
        drag = { grip, row, from, to: from, id: e.pointerId, startY: e.clientY, startScroll: window.scrollY, y: e.clientY, timer: null };
        row.dataset.touchDrag = 'true';
        e.preventDefault();
    };

    const mark = to => rows().forEach((r, i) => { if (i === to && i !== drag.from) r.dataset.touchOver = 'true'; else delete r.dataset.touchOver; });

    // The dragged row is measured where it started, or it would always be the row the finger is over.
    // How far the line has been moved: with the finger, plus however far the page has scrolled under it since.
    const offset = () => drag.y - drag.startY + (window.scrollY - drag.startScroll);
    const target = () => dropIndex(restingRects(rows().map(r => r.getBoundingClientRect()), drag.from, offset()), drag.y);

    const follow = () => {
        if (!drag) return;
        const delta = scrollDelta(drag.y, window.innerHeight);
        if (delta) window.scrollBy(0, delta);
        drag.row.style.transform = `translateY(${offset()}px)`;
        drag.to = target();
        mark(drag.to);
        drag.timer = delta ? requestAnimationFrame(follow) : null;
    };

    const onMove = e => {
        if (!drag || e.pointerId !== drag.id) return;
        drag.y = e.clientY;
        if (drag.timer === null) follow();   // while auto-scrolling, the animation loop is already following drag.y
    };

    const finish = commit => e => {
        if (!drag || e.pointerId !== drag.id) return;
        const { row, from, timer, grip, id } = drag;
        if (timer !== null) cancelAnimationFrame(timer);
        // Where the finger lifted is where the line lands: the last pointermove can be a frame or two behind it.
        if (commit) drag.y = e.clientY;
        const to = commit ? target() : drag.to;
        row.style.transform = '';
        delete row.dataset.touchDrag;
        rows().forEach(r => delete r.dataset.touchOver);
        try { grip.releasePointerCapture(id); } catch { /* already released */ }
        drag = null;
        if (commit && to !== from) ref.invokeMethodAsync('MoveTo', from, to);
    };
    const onUp = finish(true);
    const onCancel = finish(false);

    list.addEventListener('pointerdown', onPointerDownCapture, true);
    list.addEventListener('dragstart', onDragStart, true);
    list.addEventListener('pointerdown', onDown);
    list.addEventListener('pointermove', onMove);
    list.addEventListener('pointerup', onUp);
    list.addEventListener('pointercancel', onCancel);
    active.set(list, () => {
        list.removeEventListener('pointerdown', onPointerDownCapture, true);
        list.removeEventListener('dragstart', onDragStart, true);
        list.removeEventListener('pointerdown', onDown);
        list.removeEventListener('pointermove', onMove);
        list.removeEventListener('pointerup', onUp);
        list.removeEventListener('pointercancel', onCancel);
    });
}

export function detach(list) {
    active.get(list)?.();
    active.delete(list);
}
