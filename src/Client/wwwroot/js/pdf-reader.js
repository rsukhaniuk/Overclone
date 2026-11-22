// Browser layout, text selection, and link events for verified PDF page data.
export function createReader(viewport, receiver) {
    if (!(viewport instanceof HTMLElement)) throw new Error("PDF viewport required");
    const sheets = [];
    const painted = new Map();
    const maxPainted = 6;
    let hash = null, generation = 0, page = 0, zoom = 100, disposed = false;
    let requestedPage = -1, scrollTimer, ignoredTop = -1, fitAspect = 0.707;
    let measuredWidth = viewport.clientWidth, pageOffset = 0;
    let rasterTimer, rasterRequest = "";
    let selectionDrag;

    function place(node, box) {
        node.style.left = `${100 * box.left}%`;
        node.style.top = `${100 * box.top}%`;
        node.style.width = `${100 * box.width}%`;
        node.style.height = `${100 * box.height}%`;
    }
    function activate(index) {
        for (const [i, rendered] of painted) {
            if (i === index) rendered.canvas.dataset.testid = "pdf-canvas";
            else delete rendered.canvas.dataset.testid;
        }
    }
    function unpaint(index) {
        if (!painted.delete(index)) return;
        sheets[index].replaceChildren();
        sheets[index].classList.remove("pdf-painted");
    }
    function go(index) {
        if (!sheets[index]) return;
        viewport.scrollTop = sheets[index].offsetTop - sheets[0].offsetTop;
        ignoredTop = viewport.scrollTop;
    }
    function rememberPosition() {
        const sheet = sheets[page];
        if (sheet?.offsetHeight)
            pageOffset = (viewport.scrollTop - sheet.offsetTop + sheets[0].offsetTop) / sheet.offsetHeight;
    }
    function requestRaster() {
        clearTimeout(rasterTimer);
        rasterTimer = setTimeout(() => {
            const canvas = painted.get(page)?.canvas;
            if (disposed || !canvas || canvas.dataset.drawGeneration !== String(generation)
                || getComputedStyle(canvas).visibility === "hidden") return;
            const width = Math.min(4096, Math.ceil(canvas.getBoundingClientRect().width * (window.devicePixelRatio || 1)));
            const key = `${hash}:${generation}:${page}:${width}:${canvas.width}`;
            if (width < 1 || key === rasterRequest) return;
            rasterRequest = key;
            receiver.invokeMethodAsync("OnReaderRaster", generation, hash, page, width).catch(() => {});
        }, 120);
    }
    const resize = new ResizeObserver(() => {
        const width = viewport.clientWidth;
        if (width === 0 || width === measuredWidth) return;
        measuredWidth = width;
        cancelAnimationFrame(scrollTimer);
        scrollTimer = 0;
        const sheet = sheets[page];
        if (sheet) {
            viewport.scrollTop = sheet.offsetTop - sheets[0].offsetTop + pageOffset * sheet.offsetHeight;
            ignoredTop = viewport.scrollTop;
        }
        requestRaster();
    });
    function request(index) {
        if (disposed || !hash || !sheets[index] || index === page || index === requestedPage) return;
        requestedPage = index;
        receiver.invokeMethodAsync("OnReaderPage", hash, index).catch(() => { requestedPage = -1; });
    }
    function scrolled() {
        if (scrollTimer) return;
        if (!viewport.clientHeight || viewport.clientWidth !== measuredWidth || Math.abs(viewport.scrollTop - ignoredTop) < 1) return;
        ignoredTop = -1;
        rememberPosition();
        scrollTimer = requestAnimationFrame(() => {
            scrollTimer = 0;
            const position = viewport.scrollTop + viewport.clientHeight * 0.42;
            let first = 0, last = sheets.length - 1;
            while (first < last) {
                const middle = Math.floor((first + last + 1) / 2);
                if (sheets[middle].offsetTop <= position) first = middle;
                else last = middle - 1;
            }
            request(first);
        });
    }
    function clicked(event) {
        const target = event.target.closest("[data-pdf-destination]");
        if (target) {
            event.preventDefault();
            const index = Number(target.dataset.pdfDestination);
            if (sheets[index]) { go(index); request(index); }
            return;
        }
        if (event.button !== 0 || event.target.closest("a") || !window.getSelection()?.isCollapsed) return;
        const rendered = painted.get(page);
        if (rendered?.canvas.dataset.drawGeneration !== String(generation) || !sheets[page]?.contains(event.target)) return;
        const rect = rendered.canvas.getBoundingClientRect();
        const x = (event.clientX - rect.left) / rect.width, y = (event.clientY - rect.top) / rect.height;
        if (x >= 0 && x <= 1 && y >= 0 && y <= 1)
            receiver.invokeMethodAsync("OnPointer", generation, hash, page, x, y, event.detail >= 2).catch(() => {});
    }

    function closestGlyph(sheet, x, y) {
        let match, distance = Infinity;
        for (const line of sheet?.querySelectorAll(".pdf-text-glyph") ?? []) {
            const box = line.getBoundingClientRect();
            if (!box.width || !box.height) continue;
            const dx = Math.max(box.left - x, x - box.right, 0);
            const dy = Math.max(box.top - y, y - box.bottom, 0);
            const candidate = Math.hypot(dx, dy);
            if (candidate <= distance) { match = line; distance = candidate; }
        }
        return match;
    }
    function caretOnGlyph(line, x, y) {
        const box = line.getBoundingClientRect();
        const clamp = (point, start, size) => {
            const inset = Math.min(0.25, size / 3);
            return Math.min(start + size - inset, Math.max(start + inset, point));
        };
        const px = clamp(x, box.left, box.width), py = clamp(y, box.top, box.height);
        const position = document.caretPositionFromPoint?.(px, py);
        const range = position ? null : document.caretRangeFromPoint?.(px, py);
        const node = line.firstChild;
        if (position?.offsetNode === node) return { node, offset: position.offset };
        if (range?.startContainer === node) return { node, offset: range.startOffset };
        const rotation = Number(line.dataset.rotation);
        const horizontal = rotation % 180 === 0;
        let fraction = horizontal ? (px - box.left) / box.width : (py - box.top) / box.height;
        if (rotation >= 180) fraction = 1 - fraction;
        if (getComputedStyle(line).direction === "rtl") fraction = 1 - fraction;
        const text = node.textContent;
        let offset = Math.round(fraction * text.length);
        if (/[\uDC00-\uDFFF]/u.test(text[offset] ?? "")) offset++;
        return { node, offset };
    }
    function startSelection(event) {
        if (event.pointerType !== "mouse" || event.button !== 0 || event.shiftKey || event.detail >= 2 || event.target.closest("a")) return;
        const sheet = event.target.closest(".pdf-painted");
        const line = event.target.closest(".pdf-text-glyph") ?? closestGlyph(sheet, event.clientX, event.clientY);
        if (!line) return;
        // Use the native glyph geometry for both text and margin drags.
        // Browser caret hit testing alone can anchor before an absolute row.
        const anchor = caretOnGlyph(line, event.clientX, event.clientY);
        // Otherwise Chromium starts a second, native margin selection whose
        // anchor can be the beginning of the page and overwrites our range.
        event.preventDefault();
        window.getSelection()?.collapse(anchor.node, anchor.offset);
        selectionDrag = { pointer: event.pointerId, sheet, x: event.clientX, y: event.clientY, anchor };
    }
    function extendSelection(event) {
        const drag = selectionDrag;
        if (drag?.pointer !== event.pointerId || !drag.anchor.node.isConnected) return;
        if (!(event.buttons & 1)) { selectionDrag = undefined; return; }
        if (!drag.started) {
            if (Math.hypot(event.clientX - drag.x, event.clientY - drag.y) < 3) return;
            drag.started = true;
            viewport.focus({ preventScroll: true });
            viewport.setPointerCapture(event.pointerId);
        }
        const sheet = document.elementsFromPoint(event.clientX, event.clientY).map(node => node.closest(".pdf-painted")).find(Boolean);
        const line = closestGlyph(viewport.contains(sheet) ? sheet : drag.sheet, event.clientX, event.clientY);
        if (!line) return;
        const end = caretOnGlyph(line, event.clientX, event.clientY);
        window.getSelection()?.setBaseAndExtent(drag.anchor.node, drag.anchor.offset, end.node, end.offset);
    }
    function stopSelection(event) {
        if (selectionDrag?.pointer === event.pointerId) selectionDrag = undefined;
    }
    function suppressNativeDrag(event) {
        if (selectionDrag?.started) event.preventDefault();
    }
    function selectWord(event) {
        const glyph = event.target.closest(".pdf-text-glyph");
        const row = glyph?.closest(".pdf-text-line");
        if (!row || !Intl.Segmenter) return;
        const caret = caretOnGlyph(glyph, event.clientX, event.clientY);
        const walker = document.createTreeWalker(row, NodeFilter.SHOW_TEXT);
        const nodes = [];
        let length = 0, offset = 0, node;
        while ((node = walker.nextNode())) {
            if (node === caret.node) offset = length + caret.offset;
            nodes.push({ node, start: length });
            length += node.length;
        }
        const segment = [...new Intl.Segmenter(undefined, { granularity: "word" }).segment(row.textContent)]
            .find(part => part.isWordLike && part.index <= offset && offset < part.index + part.segment.length);
        if (!segment) return;
        const start = nodes.find(item => item.start + item.node.length > segment.index);
        const endOffset = segment.index + segment.segment.length;
        const end = nodes.find(item => item.start + item.node.length >= endOffset);
        if (!start || !end) return;
        event.preventDefault();
        window.getSelection()?.setBaseAndExtent(start.node, segment.index - start.start, end.node, endOffset - end.start);
    }
    function selectPageText() {
        const text = sheets[page]?.querySelector(".pdf-text-layer");
        const selection = window.getSelection();
        if (disposed || !text?.textContent.trim() || !selection) return false;
        const tools = viewport.parentElement?.querySelector(".pdf-more-option");
        if (tools) tools.open = false;
        viewport.focus({ preventScroll: true });
        selection.selectAllChildren(text);
        return true;
    }
    function readerKeys(event) {
        if ((event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey && event.key.toLowerCase() === "a"
            && selectPageText()) event.preventDefault();
    }
    function makeTextLayer(content, width, height, context) {
        const layer = document.createElement("div");
        layer.className = "pdf-text-layer";
        const rotation = content?.rotation ?? 0;
        let line = [];
        function flush() {
            if (!line.length) return;
            const boxes = line.map(glyph => glyph.box).filter(Boolean);
            if (!boxes.length) {
                layer.append(document.createTextNode(line.map(glyph => glyph.text).join("")));
                line = [];
                return;
            }
            const left = Math.min(...boxes.map(box => box.left)), top = Math.min(...boxes.map(box => box.top));
            const right = Math.max(...boxes.map(box => box.left + box.width));
            const bottom = Math.max(...boxes.map(box => box.top + box.height));
            const row = document.createElement("span");
            row.className = "pdf-text-line";
            place(row, { left, top, width: right - left, height: bottom - top });
            for (const glyph of line) {
                const box = glyph.box;
                if (!box) { row.append(document.createTextNode(glyph.text)); continue; }
                const span = document.createElement("span");
                span.className = "pdf-text-glyph";
                span.textContent = glyph.text;
                span.dir = "auto";
                span.dataset.rotation = String(rotation);
                const vertical = rotation === 90 || rotation === 270;
                const fontSize = vertical ? box.width * width : box.height * height;
                const length = vertical ? box.height * height : box.width * width;
                span.style.left = `${100 * ((rotation === 90 || rotation === 180 ? box.left + box.width : box.left) - left) / (right - left)}%`;
                span.style.top = `${100 * ((rotation === 180 || rotation === 270 ? box.top + box.height : box.top) - top) / (bottom - top)}%`;
                span.style.fontSize = `${100 * fontSize / height}cqh`;
                context.font = `${fontSize}px serif`;
                const measured = context.measureText(glyph.text).width;
                span.style.transform = `rotate(${rotation}deg) scaleX(${measured > 0 ? length / measured : 1})`;
                row.append(span);
            }
            layer.append(row);
            line = [];
        }
        // Native gaps (table columns, kerning and justified spaces) cannot be
        // represented by stretching a whole line. Preserve each glyph's box.
        for (const glyph of content?.glyphs ?? []) {
            if (/^[\r\n]$/.test(glyph.text)) {
                flush();
                layer.append(document.createTextNode(glyph.text));
            } else line.push(glyph);
        }
        flush();
        return layer;
    }
    function makeLinks(links) {
        const layer = document.createElement("div");
        layer.className = "pdf-links";
        for (const link of links) {
            const anchor = document.createElement("a");
            if (link.pageIndex != null && sheets[link.pageIndex]) {
                anchor.href = `#${sheets[link.pageIndex].id}`;
                anchor.dataset.pdfDestination = String(link.pageIndex);
                anchor.setAttribute("aria-label", `Go to PDF page ${link.pageIndex + 1}`);
            } else if (link.address) {
                const destination = new URL(link.address);
                if (destination.protocol !== "http:" && destination.protocol !== "https:") continue;
                anchor.href = destination.href;
                anchor.target = "_blank";
                anchor.rel = "noopener noreferrer";
                anchor.setAttribute("aria-label", `Open ${destination.href} in a new tab`);
            } else continue;
            place(anchor, link.box);
            layer.append(anchor);
        }
        return layer;
    }

    viewport.addEventListener("scroll", scrolled, { passive: true });
    viewport.addEventListener("click", clicked);
    viewport.addEventListener("dblclick", selectWord);
    viewport.addEventListener("pointerdown", startSelection);
    viewport.addEventListener("pointermove", extendSelection);
    viewport.addEventListener("pointerup", stopSelection);
    viewport.addEventListener("pointercancel", stopSelection);
    viewport.addEventListener("lostpointercapture", stopSelection);
    viewport.addEventListener("selectstart", suppressNativeDrag);
    viewport.addEventListener("keydown", readerKeys);
    resize.observe(viewport);
    window.addEventListener("resize", requestRaster);
    return {
        selectPageText,
        configure(nextHash, count, index, percent) {
            if (disposed) return;
            const changed = hash !== nextHash;
            if (changed) {
                for (const i of painted.keys()) unpaint(i);
                hash = nextHash;
                requestedPage = -1;
            }
            if (!hash) count = 0;
            // Keep the document's scroll space until a recompile reports its page count.
            if (hash && count === 0) return;
            while (sheets.length > count) sheets.pop().remove();
            while (sheets.length < count) {
                const sheet = document.createElement("div");
                sheet.className = "pdf-surface pdf-fit pdf-sheet";
                sheet.id = `compiled-pdf-page-${sheets.length + 1}`;
                sheet.dataset.pageNumber = String(sheets.length + 1);
                sheet.style.aspectRatio = String(fitAspect);
                sheets.push(sheet);
                viewport.append(sheet);
            }
            const priorPage = page;
            page = Math.min(index, Math.max(0, count - 1));
            if (zoom !== percent) {
                const sheet = sheets[page];
                const fraction = sheet?.offsetHeight ? (viewport.scrollTop - sheet.offsetTop + sheets[0].offsetTop) / sheet.offsetHeight : 0;
                zoom = percent;
                viewport.style.setProperty("--pdf-zoom", String(zoom / 100));
                if (sheet && viewport.clientHeight && page === priorPage) {
                    viewport.scrollTop = sheet.offsetTop - sheets[0].offsetTop + fraction * sheet.offsetHeight;
                    ignoredTop = viewport.scrollTop;
                }
            }
            if (page !== priorPage && page !== requestedPage && viewport.clientHeight) go(page);
            requestedPage = -1;
            if (viewport.clientWidth === measuredWidth) rememberPosition();
            activate(page);
            requestRaster();
        },
        invalidate(value) {
            if (disposed || value <= generation) return;
            generation = value;
            for (const rendered of painted.values()) rendered.highlights.replaceChildren();
        },
        measure() { return Math.max(64, Math.min(1600, viewport.clientWidth || 360)); },
        reuse(value, documentHash, index, width, height) {
            if (disposed || value !== generation || documentHash !== hash || index !== page) return false;
            const rendered = painted.get(index);
            if (!rendered || rendered.canvas.width !== width || rendered.canvas.height !== height) return false;
            rendered.canvas.dataset.drawGeneration = String(value);
            activate(page);
            requestRaster();
            return true;
        },
        draw(value, documentHash, index, width, height, rgba, content) {
            if (disposed || value !== generation || documentHash !== hash || !sheets[index]) return false;
            if (!Number.isInteger(width) || !Number.isInteger(height) || width < 1 || height < 1 || width * height > 4000000
                || !(rgba instanceof Uint8Array) || rgba.byteLength !== width * height * 4) throw new Error("PDF raster shape");
            const sheet = sheets[index];
            let rendered = painted.get(index);
            const canvas = rendered?.canvas ?? document.createElement("canvas");
            canvas.width = width;
            canvas.height = height;
            const pixels = new Uint8ClampedArray(rgba.buffer, rgba.byteOffset, rgba.byteLength);
            const context = canvas.getContext("2d");
            context.putImageData(new ImageData(pixels, width, height), 0, 0);
            if (!rendered) {
                canvas.setAttribute("aria-label", `PDF page ${index + 1}`);
                canvas.draggable = false;
                const highlights = document.createElement("div");
                highlights.className = "pdf-highlights";
                sheet.classList.add("pdf-painted");
                sheet.replaceChildren(canvas, makeTextLayer(content, width, height, context), highlights, makeLinks(content?.links ?? []));
                rendered = { canvas, highlights };
                painted.set(index, rendered);
                if (index === 0) {
                    fitAspect = width / height;
                    for (const [i, other] of sheets.entries()) if (!painted.has(i)) other.style.aspectRatio = String(fitAspect);
                }
                sheet.style.aspectRatio = `${width} / ${height}`;
            }
            rendered.canvas.dataset.pdfHash = hash;
            rendered.canvas.dataset.pdfPage = String(index);
            rendered.canvas.dataset.drawGeneration = String(value);
            while (painted.size > maxPainted || [...painted.values()].reduce((sum, item) => sum + item.canvas.width * item.canvas.height, 0) > 8_000_000) {
                const farthest = [...painted.keys()].reduce((a, b) => Math.abs(a - page) >= Math.abs(b - page) ? a : b);
                unpaint(farthest);
            }
            activate(page);
            requestRaster();
            return true;
        },
        highlights(value, boxes) {
            const rendered = painted.get(page);
            if (disposed || value !== generation || rendered?.canvas.dataset.drawGeneration !== String(value)) return;
            rendered.highlights.replaceChildren(...boxes.map(({ box, isSource, title }) => {
                const node = document.createElement("span");
                node.className = isSource ? "pdf-highlight pdf-source-highlight" : "pdf-highlight";
                node.dataset.testid = isSource ? "source-pdf-highlight" : "glyph-highlight";
                if (title) node.title = title;
                place(node, box);
                return node;
            }));
            const source = rendered.highlights.querySelector(".pdf-source-highlight");
            if (source && viewport.clientHeight) {
                const marker = source.getBoundingClientRect(), view = viewport.getBoundingClientRect();
                if (marker.top < view.top || marker.bottom > view.bottom) {
                    viewport.scrollTop += marker.top - view.top - Math.min(80, viewport.clientHeight / 5);
                    ignoredTop = viewport.scrollTop;
                    rememberPosition();
                }
            }
        },
        dispose() {
            disposed = true;
            cancelAnimationFrame(scrollTimer);
            clearTimeout(rasterTimer);
            resize.disconnect();
            window.removeEventListener("resize", requestRaster);
            viewport.removeEventListener("scroll", scrolled);
            viewport.removeEventListener("click", clicked);
            viewport.removeEventListener("dblclick", selectWord);
            viewport.removeEventListener("pointerdown", startSelection);
            viewport.removeEventListener("pointermove", extendSelection);
            viewport.removeEventListener("pointerup", stopSelection);
            viewport.removeEventListener("pointercancel", stopSelection);
            viewport.removeEventListener("lostpointercapture", stopSelection);
            viewport.removeEventListener("selectstart", suppressNativeDrag);
            viewport.removeEventListener("keydown", readerKeys);
            viewport.replaceChildren();
            painted.clear();
        }
    };
}
