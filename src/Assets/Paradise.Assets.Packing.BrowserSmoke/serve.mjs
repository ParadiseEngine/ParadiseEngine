// A local static server for the published smoke host; no third-party packages needed.
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { resolve, relative, isAbsolute, extname } from 'node:path';

if (!process.argv[2]) throw new Error('Usage: node serve.mjs <published wwwroot> [port]');
const root = resolve(process.argv[2]);
const types = { '.html': 'text/html', '.js': 'text/javascript', '.json': 'application/json', '.wasm': 'application/wasm' };
createServer(async (request, response) => {
    try {
        const name = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
        const path = resolve(root, '.' + (name === '/' ? '/index.html' : name));
        const inside = relative(root, path);
        if (inside.startsWith('..') || isAbsolute(inside)) {
            response.writeHead(403).end();
            return;
        }
        const bytes = await readFile(path);
        response.writeHead(200, { 'Content-Type': types[extname(path)] || 'application/octet-stream', 'Cache-Control': 'no-store' });
        response.end(bytes);
    } catch {
        response.writeHead(404).end();
    }
}).listen(Number(process.argv[3] || 8191), '127.0.0.1', () => console.log(`Serving ${root} on http://127.0.0.1:${process.argv[3] || 8191}`));
