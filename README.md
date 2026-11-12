# SchoolMonitoring (SafeClass Monitor UI prototype)

Open `index.html` in any browser. No install or server needed.

- `index.html`   page structure (4 sections: Dashboard, Classrooms, Alerts, Settings)
- `css/styles.css` colors and layout (navy / red / green / gray), phone + desktop
- `js/data.js`   SAMPLE DATA ONLY: classrooms, alerts, alert types
- `js/app.js`    all behavior; actions are in section 5 ("Actions")

Connecting ESP32 + cloud later: replace `buildSampleData()` in data.js with a
database load, update `state` when a device message arrives, then call `renderAll()`.

## Layout
- `index.html`  single-file version (CSS and JS embedded). Open this one.
- `src/`        the same app split into index.html, css/, js/ for development.
