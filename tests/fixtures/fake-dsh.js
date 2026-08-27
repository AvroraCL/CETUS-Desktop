const http = require('http');

const args = process.argv.slice(2);
const portIndex = args.indexOf('--port');
if (!args.includes('web') || portIndex < 0 || args.length !== 3) {
  process.exit(41);
}

const port = Number(args[portIndex + 1]);
if (!Number.isInteger(port) || port < 1 || port > 65535) {
  process.exit(42);
}

const server = http.createServer((request, response) => {
  response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
  response.end('<!doctype html><html><body><div id="root">Cetus smoke</div></body></html>');
});

server.listen(port, '127.0.0.1');

const stop = () => server.close(() => process.exit(0));
process.on('SIGINT', stop);
process.on('SIGTERM', stop);
