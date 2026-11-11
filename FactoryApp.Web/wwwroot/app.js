// Header name must match AddAntiforgery HeaderName in Program.cs.
document.body.addEventListener('htmx:configRequest', function (event) {
    var meta = document.querySelector('meta[name="csrf-token"]');
    if (!meta) return;
    if (new URL(event.detail.path, location.href).origin !== location.origin) return;
    event.detail.headers['RequestVerificationToken'] = meta.content;
});
