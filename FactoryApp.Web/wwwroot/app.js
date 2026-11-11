// htmx 2 skips 4xx swaps; 422 carries the validation message. First match wins, so 422 must precede [45]...
htmx.config.responseHandling = [{code:"204",swap:false},{code:"422",swap:true,error:false},{code:"[23]..",swap:true},{code:"[45]..",swap:false,error:true},{code:"...",swap:false}];

// Header name must match AddAntiforgery HeaderName in Program.cs.
document.body.addEventListener('htmx:configRequest', function (event) {
    var meta = document.querySelector('meta[name="csrf-token"]');
    if (!meta) return;
    if (new URL(event.detail.path, location.href).origin !== location.origin) return;
    event.detail.headers['RequestVerificationToken'] = meta.content;
});

// An expired session makes the browser follow the login redirect inside the XHR; swapping that page into a card would be wrong.
document.body.addEventListener('htmx:beforeSwap', function (event) {
    var finalUrl = event.detail.xhr && event.detail.xhr.responseURL;
    if (finalUrl && new URL(finalUrl, location.href).pathname === '/Login') {
        event.detail.shouldSwap = false;
        window.location.href = finalUrl;
    }
});

document.body.addEventListener('htmx:responseError', function (event) {
    if (event.detail.target) {
        event.detail.target.textContent = 'The request failed. Reload the page and try again.';
    }
});
