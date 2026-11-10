// Header name must match AddAntiforgery HeaderName in Program.cs.
document.body.addEventListener('htmx:configRequest', function (event) {
    var meta = document.querySelector('meta[name="csrf-token"]');
    if (!meta) return;
    event.detail.headers['RequestVerificationToken'] = meta.content;
});
