// ProjectBridge-owned composition layer. Upstream OAuth, pairing and lifecycle stay intact.
import { readFile, writeFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import path from 'node:path';
import { createRequire } from 'node:module';
const [backend, workspace, port, gateway] = process.argv.slice(2);
const key = process.env.LPB_SHARED_KEY;
const serverFile = path.join(backend, 'dist/bridge/server.js');
const source = await readFile(serverFile, 'utf8');
const anchor = 'const mcpHandler = createMcpHttpHandler(() => createMcpServer({ workspace, logger }), logger);';
if (source.split(anchor).length !== 2) throw new Error('C2C version is incompatible with the shared project bridge.');
const forwarder = `const mcpHandler = async (req, res) => {
  if (req.method !== 'POST') { res.status(405).end(); return; }
  if (!req.auth?.scopes?.includes('workspace.read')) { res.status(403).json({error:'insufficient_scope'}); return; }
  try {
    const response = await fetch(process.env.LPB_SHARED_GATEWAY, {
      method:'POST', headers:{
        'content-type':'application/json',
        authorization:'Bearer '+process.env.LPB_SHARED_KEY,
        'x-projectbridge-remote':'c2c-oauth',
        'x-projectbridge-client':String(req.auth?.clientId ?? req.auth?.client_id ?? 'c2c-oauth-client').replace(/[^A-Za-z0-9._:-]/g,'').slice(0,128)
      },
      body:JSON.stringify(req.body), signal:AbortSignal.timeout(30000)
    });
    res.status(response.status).type('application/json').send(await response.text());
  } catch { res.status(503).json({error:'shared_gateway_unavailable'}); }
};`;
// Resolve imports against the installed backend, while keeping this generated module in app state.
const requireBackend = createRequire(pathToFileURL(serverFile));
let composed = source.replace(anchor, forwarder).replace(/from "([^"]+)"/g,
  (_, specifier) => 'from ' + JSON.stringify(specifier.startsWith('node:') ? specifier :
    specifier.startsWith('.') ? new URL(specifier, pathToFileURL(serverFile)).href : pathToFileURL(requireBackend.resolve(specifier)).href));
const composedFile = path.join(path.dirname(process.argv[1]), 'shared-server.mjs');
await writeFile(composedFile, composed, 'utf8');
process.env.LPB_SHARED_GATEWAY = gateway;
process.env.LPB_SHARED_KEY = key;
const { startBridge } = await import(pathToFileURL(composedFile));
const { Logger } = await import(pathToFileURL(path.join(backend,'dist/logger/index.js')));
const { CloudflaredQuickTunnel } = await import(pathToFileURL(path.join(backend,'dist/tunnel/cloudflared.js')));
const { namedTunnelBinding, readTunnelState } = await import(pathToFileURL(path.join(backend,'dist/tunnel/state.js')));
const { Workspace } = await import(pathToFileURL(path.join(backend,'dist/workspace/manager.js')));
const logger = new Logger({name:'shared-bridge',console:true});
// .NET's health probe honors the desktop proxy settings; Node's default fetch may bypass them.
const healthFetch = async (url, options) => {
  const response = await fetch(gateway.replace(/\/mcp$/, '/probe'), {
    method:'POST', headers:{authorization:'Bearer '+key,'content-type':'application/json'},
    body:JSON.stringify({url}), signal:options?.signal
  });
  if (!response.ok) throw new Error('Local health probe unavailable');
  const result = await response.json();
  if (!result.ready) throw new Error(result.detail);
  return new Response(JSON.stringify({service:'c2c-bridge',status:'ok'}), {status:200});
};
const binding = namedTunnelBinding(readTunnelState(new Workspace(workspace).id));
let tunnel;
if (!binding) {
  tunnel = new CloudflaredQuickTunnel(logger, process.env.C2C_CLOUDFLARED_PATH, {startTimeoutMs:45000, fetchImpl:healthFetch});
  const start = tunnel.start.bind(tunnel);
  const stop = tunnel.stop.bind(tunnel);
  let generation = 0;
  tunnel.stop = () => { generation++; return stop(); };
  tunnel.start = async port => {
    const current = generation;
    for (let attempt = 0; attempt < 2; attempt++) {
      tunnel.startTimeoutMs = attempt === 0 ? 45000 : 30000;
      try { return await start(port); } catch(error) {
        if (attempt === 1 || generation !== current)
          throw new Error(error.message + (tunnel.lastError ? ': '+tunnel.lastError : ' (no public address received)'));
        logger.warn('Initial tunnel connection failed; retrying once: '+error.message);
        await new Promise(resolve => setTimeout(resolve, 2000));
        if (generation !== current) throw new Error('Tunnel start stopped');
      }
    }
  };
}
await startBridge({workspaceRoot:workspace,port:Number(port),logger,...(tunnel ? {tunnelProvider:tunnel}: {})});
