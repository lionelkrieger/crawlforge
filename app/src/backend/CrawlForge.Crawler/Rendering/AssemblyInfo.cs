using System.Runtime.CompilerServices;

// Component tests drive the internal CDP composition seams (browser factory,
// browser leases) against a scripted loopback CDP server.
[assembly: InternalsVisibleTo("CrawlForge.Crawler.Tests")]
