// htmx 2 skips 4xx swaps; 422 carries the validation message. First match wins, so 422 must precede [45]...
htmx.config.responseHandling = [{code:"204",swap:false},{code:"422",swap:true,error:false},{code:"[23]..",swap:true},{code:"[45]..",swap:false,error:true},{code:"...",swap:false}];

// Header name must match AddAntiforgery HeaderName in Program.cs.
document.body.addEventListener('htmx:configRequest', function (event) {
    var meta = document.querySelector('meta[name="csrf-token"]');
    if (!meta) return;
    if (new URL(event.detail.path, location.href).origin !== location.origin) return;
    event.detail.headers['RequestVerificationToken'] = meta.content;
});
