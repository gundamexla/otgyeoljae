// Loaded only by tests. All outbound requests are intercepted, so tests cannot spend API credits.
globalThis.fetch = async (url, options) => {
  const host = new URL(url).hostname;
  const provider = host === 'agent-os-biz.adot.ai' ? 'SKT' : host === 'api.openai.com' ? 'OPENAI' : null;
  if (!provider) throw new Error('Unexpected outbound request blocked by test fixture');
  const input = JSON.parse(options.body).messages.at(-1).content;
  let mode = process.env[`MOCK_${provider}`] || 'success';
  if (mode === 'race') {
    if (input === 'slow-success') { await new Promise(resolve => setTimeout(resolve, 120)); mode = 'success'; }
    else mode = 'http';
  }
  if (mode === 'http') return new Response('DO_NOT_EXPOSE_PROVIDER_BODY', { status:401 });
  if (mode === 'timeout') throw Object.assign(new Error('DO_NOT_EXPOSE_PROVIDER_BODY'), { name:'TimeoutError' });
  if (mode === 'network') throw new Error('DO_NOT_EXPOSE_PROVIDER_BODY');
  const content = mode === 'malformed' ? '{not-json' : mode === 'invalid' ? '{}' : JSON.stringify({ intent:'outfit', occasion:'면접', wanted_item:'재킷', evidence:[] });
  return Response.json({ choices:[{ message:{content} }] });
};
