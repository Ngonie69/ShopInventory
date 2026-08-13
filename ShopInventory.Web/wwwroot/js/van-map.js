// The round on the ground — /van-sales/activity's map rail.
//
// Draws one numbered pin per call that recorded a position, in the order the rep made them, joined by
// a dashed line. Calls with no fix are not on the map at all and are numbered "–" in the table beside
// it, because a pin dropped at a guessed position is the one thing this page must never show: the
// whole point of it is telling a proven position from an unproven one.
//
// Leaflet is loaded from wwwroot/lib rather than a CDN — see that folder's README — and lazily, on
// first render, so the other sixty pages do not carry 145 KB they never use. Loading it from a <script>
// this file appends is allowed by `script-src 'self'`; the tiles are <img> loads and ride on
// `img-src ... https:`, so neither needs a CSP change.

(function () {
    'use strict';

    // The design's tile treatment. OpenStreetMap ships one raster set, so a dark map is that set
    // inverted and cooled rather than a second download.
    var DARK_TILES = 'invert(1) hue-rotate(186deg) brightness(0.76) contrast(0.94) saturate(0.5)';

    var maps = {};
    var leafletPromise = null;

    function loadLeaflet() {
        if (window.L) return Promise.resolve(window.L);
        if (leafletPromise) return leafletPromise;

        leafletPromise = new Promise(function (resolve, reject) {
            if (!document.getElementById('leaflet-css')) {
                var css = document.createElement('link');
                css.id = 'leaflet-css';
                css.rel = 'stylesheet';
                css.href = 'lib/leaflet/leaflet.css';
                document.head.appendChild(css);
            }

            var script = document.createElement('script');
            script.id = 'leaflet-js';
            script.src = 'lib/leaflet/leaflet.js';
            script.onload = function () {
                window.L ? resolve(window.L) : reject(new Error('Leaflet loaded but defined no L.'));
            };
            script.onerror = function () {
                // Cleared so a later render retries: the first failure is usually a cold deploy
                // still copying wwwroot, not a missing file.
                leafletPromise = null;
                reject(new Error('Leaflet could not be loaded from lib/leaflet/leaflet.js.'));
            };
            document.head.appendChild(script);
        });

        return leafletPromise;
    }

    function isDark() {
        return document.documentElement.getAttribute('data-theme') === 'dark';
    }

    /** A colour the page's stylesheet owns, so the line follows the theme with everything else. */
    function token(el, name, fallback) {
        var value = getComputedStyle(el).getPropertyValue(name);
        return value && value.trim() ? value.trim() : fallback;
    }

    function applyTheme(entry) {
        var pane = entry.map.getPane('tilePane');
        if (pane) pane.style.filter = isDark() ? DARK_TILES : 'none';
        if (entry.line) entry.line.setStyle({ color: token(entry.el, '--vs-map-line', '#796cbf') });
    }

    function draw(entry) {
        var L = window.L;
        var stops = entry.stops;
        var map = entry.map;

        if (!stops.length) {
            entry.markers.forEach(function (marker) { marker.remove(); });
            entry.markers = [];
            if (entry.line) { entry.line.remove(); entry.line = null; }
            entry.key = '';
            return;
        }

        var points = stops.map(function (stop) { return [stop.lat, stop.lon]; });

        // Markers are created before the line and reused in place: a vector-layer failure must never
        // take the pins down with it, and re-adding them on every state change makes the whole rail
        // flicker each time a row is clicked.
        stops.forEach(function (stop, i) {
            var on = i === entry.active;
            var size = on ? 26 : 20;
            var classes = 'vs-pin' + (stop.stale ? ' vs-pin-stale' : '') + (on ? ' vs-pin-on' : '');
            var icon = L.divIcon({
                className: '',
                iconSize: [size, size],
                iconAnchor: [size / 2, size / 2],
                html: '<div class="' + classes + '" style="width:' + size + 'px;height:' + size + 'px">'
                    + stop.n + '</div>'
            });

            var marker = entry.markers[i];
            if (marker) {
                marker.setLatLng([stop.lat, stop.lon]);
                marker.setIcon(icon);
                marker.setZIndexOffset(on ? 1000 : 0);
                marker.unbindTooltip();
            } else {
                marker = L.marker([stop.lat, stop.lon], { icon: icon, zIndexOffset: on ? 1000 : 0 });
                marker.addTo(map);
                entry.markers[i] = marker;
            }

            // textContent, not innerHTML: the customer name is data and this tooltip is the one place
            // on the page it would be parsed as markup.
            var tip = document.createElement('div');
            var name = document.createElement('b');
            name.textContent = stop.name;
            tip.appendChild(name);
            tip.appendChild(document.createElement('br'));
            tip.appendChild(document.createTextNode(stop.time + (stop.stale ? ' · last-known fix' : '')));

            marker.bindTooltip(tip, { direction: 'top', offset: [0, -size / 2], className: 'vs-pin-tip' });
        });

        while (entry.markers.length > stops.length) entry.markers.pop().remove();

        try {
            if (entry.line) {
                entry.line.setLatLngs(points);
            } else {
                entry.line = L.polyline(points, {
                    color: token(entry.el, '--vs-map-line', '#796cbf'),
                    weight: 1.5,
                    opacity: 0.55,
                    dashArray: '4 5'
                }).addTo(map);
            }
        } catch (e) {
            // The numbered pins already carry the call order, so a failed polyline costs nothing
            // that would be worth blanking the map for.
        }

        // Refit only when the round itself changed. Clicking through the calls of one round should
        // pan to each in turn, not re-frame the whole day underneath the reader.
        var key = stops.map(function (s) {
            return s.n + ':' + s.lat.toFixed(4) + ',' + s.lon.toFixed(4);
        }).join('|');

        if (key !== entry.key) {
            entry.key = key;
            map.fitBounds(L.latLngBounds(points).pad(0.22), { animate: false });
        } else if (entry.active >= 0 && stops[entry.active]) {
            map.panTo([stops[entry.active].lat, stops[entry.active].lon], { animate: true });
        }
    }

    function create(el, L) {
        var map = L.map(el, {
            zoomControl: true,
            // Left off deliberately: the map sits in a column the reader scrolls past, and a wheel
            // that zooms the map instead of the page traps them in it.
            scrollWheelZoom: false,
            worldCopyJump: false,
            attributionControl: true
        });

        L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
            attribution: '&copy; OpenStreetMap contributors',
            maxZoom: 19
        }).addTo(map);

        // Harare, until the first round frames itself. Without a view Leaflet throws on the first
        // layer added.
        map.setView([-17.8292, 31.0522], 11);

        return map;
    }

    window.vanMap = {
        /**
         * Draw a round. Safe to call on every render: the map is built once per element and only the
         * pins move afterwards.
         *
         * @param {string} elementId   the container div
         * @param {Array}  stops       {n, name, time, lat, lon, stale}, in call order
         * @param {number} activeIndex the call to highlight, or -1
         */
        render: function (elementId, stops, activeIndex) {
            var el = document.getElementById(elementId);
            if (!el) return;

            var entry = maps[elementId];
            if (entry && entry.el !== el) {
                // Blazor replaced the node under us — the old map is pointing at a detached div.
                window.vanMap.dispose(elementId);
                entry = null;
            }

            if (entry) {
                entry.stops = stops || [];
                entry.active = typeof activeIndex === 'number' ? activeIndex : -1;
                draw(entry);
                applyTheme(entry);
                return;
            }

            // Claim the slot before the await, or two renders in the same frame build two maps into
            // one div and Leaflet throws "Map container is already initialized".
            entry = maps[elementId] = {
                el: el,
                map: null,
                markers: [],
                line: null,
                key: '',
                stops: stops || [],
                active: typeof activeIndex === 'number' ? activeIndex : -1
            };

            loadLeaflet().then(function (L) {
                // Disposed, or replaced, while Leaflet was in flight.
                if (maps[elementId] !== entry || !el.isConnected) return;

                entry.map = create(el, L);
                applyTheme(entry);
                draw(entry);

                requestAnimationFrame(function () { entry.map && entry.map.invalidateSize(); });

                // The rail is sticky beside a list that grows and shrinks as rounds expand, so the
                // container resizes without the window ever doing so.
                try {
                    entry.resize = new ResizeObserver(function () {
                        entry.map && entry.map.invalidateSize();
                    });
                    entry.resize.observe(el);
                } catch (e) { /* older browser: the initial size is still right */ }

                // Follows the topbar's theme toggle without the page having to re-render for it.
                try {
                    entry.theme = new MutationObserver(function () { applyTheme(entry); });
                    entry.theme.observe(document.documentElement, {
                        attributes: true,
                        attributeFilter: ['data-theme']
                    });
                } catch (e) { /* the map still opens in whichever theme was current */ }
            }).catch(function (err) {
                delete maps[elementId];
                if (el.isConnected) el.classList.add('vs-map-failed');
                console.error('van-map:', err);
            });
        },

        dispose: function (elementId) {
            var entry = maps[elementId];
            if (!entry) return;

            if (entry.resize) entry.resize.disconnect();
            if (entry.theme) entry.theme.disconnect();
            if (entry.map) entry.map.remove();

            delete maps[elementId];
        }
    };
})();
