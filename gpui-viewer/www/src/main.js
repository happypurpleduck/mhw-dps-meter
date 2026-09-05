// Boots the wasm build of the GPUI viewer. `?logs=<url>` points it at a served folder
// that contains index.json; without it the bundled sample logs are shown.
async function init() {
  const loading = document.getElementById('loading')
  try {
    const wasm = await import('./wasm/mhw_log_viewer.js')
    await wasm.default()
    const logs = new URLSearchParams(location.search).get('logs') || undefined
    await wasm.run(logs)
    loading?.remove()
  } catch (error) {
    console.error('failed to start', error)
    if (loading) {
      loading.innerHTML = `<div class="error"><h2>Could not start the viewer</h2>
        <p>${String(error?.message ?? error)}</p>
        <p>GPUI needs WebGPU or WebGL2. Build the wasm first with <code>scripts/build-wasm.sh</code>.</p></div>`
    }
  }
}
init()
