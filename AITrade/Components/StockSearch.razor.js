// While a suggestion is highlighted, Enter picks it (handled by the component) instead of submitting the form.
export function init(input) {
    input.addEventListener('keydown', e => {
        if (e.key === 'Enter' && input.getAttribute('aria-expanded') === 'true' && input.hasAttribute('aria-activedescendant')) {
            e.preventDefault();
        }
    });
}
