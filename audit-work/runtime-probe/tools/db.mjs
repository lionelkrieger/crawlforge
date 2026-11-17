import { DatabaseSync } from 'node:sqlite';
const db = new DatabaseSync(process.env.APPDATA + '/CrawlForge/crawlforge.db');
const sql = process.argv[2];
const st = db.prepare(sql);
console.log(JSON.stringify(/^\s*select|pragma/i.test(sql) ? st.all() : st.run(), null, 1));
