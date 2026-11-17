export default async (h) => {
  const cdp = await h.page.context().newCDPSession(h.page);
  const msgs = [];
  cdp.on('Runtime.consoleAPICalled', e => msgs.push(e.type + ' ' + e.args.map(a => a.value ?? a.description).join(' ')));
  cdp.on('Runtime.exceptionThrown', e => msgs.push('EXC ' + JSON.stringify(e.exceptionDetails).slice(0, 800)));
  await cdp.send('Runtime.enable');
  await h.page.waitForTimeout(1500);
  const info = await h.page.evaluate(() => ({ url: location.href, rootHtml: document.getElementById('root')?.innerHTML.slice(0, 300), bodyLen: document.body.innerHTML.length }));
  return { info, msgs: msgs.slice(-30) };
};
