// Tells the app which kind of screen it is on (see ScreenInfo.cs), so it can change what it offers on a phone.
// The same two media queries the stylesheet uses (`narrow` and `wide` in styles/tailwind.css), so layout and logic agree.

const PHONE = '(max-width: 600px)';
const DESKTOP = '(min-width: 56rem)';

let queries = [];
let listener = null;

/** 'phone' up to 600px wide, 'desktop' from 56rem (the sidebar's breakpoint), 'tablet' between. */
export function classify(isPhone, isDesktop) {
    return isPhone ? 'phone' : isDesktop ? 'desktop' : 'tablet';
}

function current() {
    return classify(queries[0].matches, queries[1].matches);
}

/** Starts reporting changes to `ref` (ScreenInfo.OnScreenChanged) and returns the class right now. */
export function attach(ref) {
    detach();
    queries = [window.matchMedia(PHONE), window.matchMedia(DESKTOP)];
    listener = () => ref.invokeMethodAsync('OnScreenChanged', current());
    queries.forEach(q => q.addEventListener('change', listener));
    return current();
}

export function detach() {
    if (listener) queries.forEach(q => q.removeEventListener('change', listener));
    queries = [];
    listener = null;
}
