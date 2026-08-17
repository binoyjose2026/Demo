You are a design reviewer

Because the review of project like this can take a lot of questions and numerous days. I am limiting the scope of the review to one oir two review items. I don't want this to turn into a never ending back and forth where we're debating every possible axis of the system at once - scalability alone is already a big enough topic to keep us busy, so let's just go deep on that first and leave the rest (security posture in general, cost, team process etc) for a later pass once this one is actually done and reviewed properly.

Scope of the review: Scalability

Let us assume we have a 1 million link creation a day and 10 million fetches a day. I'm picking these numbers because they feel like a believable "we actually got some traction" scenario, not a toy example and not a hyperscale fantasy either - somewhere realistic enough that the trade offs we talk about actually matter.

That means 1 million can grow to 5 mn in 5 years and 10 mn could grow to 100mn ferches a day. Basically assume roughly 5x growth over 5 years on both sides, and yes fetches will always be way higher volume than creates because that's just how a url shortener behaves - people create a link once and then it gets clicked a hundred times.

Let ud discuss how we can solve these problems. I want actual reasoning here, not just "use X because it's popular" - if something doesn't make sense at our scale I'd rather hear that plainly than get a recommendation that sounds impressive but doesn't fit.

I want you to discuss these:
1. How create can be provided with extreme scalability?

Explain why Kafka is or is not a sutable solution for it. create a specific document on this comparison `../design/considerations/05-kafka-comparison.md` - don't just default to "Kafka is the industry standard so use it," actually work through whether our numbers justify it or whether something simpler does the job just as well.

Explain if we need Outbox pattern needed here? Create a specific document. Create numbereed document for each of these considerations - I'd like every topic below to land as its own numbered file in that considerations folder so it's easy to review one thing at a time instead of one giant wall of text.

Explain why Elastic Search is a better solution for this due to extreme size. Make a comaprison between Elastic search and Sql Server - specifically thinking about the analytics/click side of things here, not the core url mapping, since that part probably still wants to stay relational.

Also create a comaprison between Elastic Search vs MongoDB here - same use case, just want to see both angles compared so the choice is defensible either way.

How can we increase the through put using output caching for public api. Create a consitaration on why we need BFF style design for public URLs and enage cloudflare based CDN to boost output cache. Basically I want the redirect path to be as cheap as possible to serve at massive volume, ideally most of that traffic never even touching our servers.

Create a redis based cache to increase frequently used data with queue size limit and distributed caching. Also explain how cache invalidation should be done - this is one of those areas where getting it wrong quietly causes stale data bugs, so don't gloss over it.

Also explain how the metadata of the files can be managed. This can be a seperate document

Explain how Bulkhead pattern must be used

Explain how timeout, retry and exponsentail back off patterns to be used
explain how jittern pattern to be used

I know these last few (bulkhead, timeout, retry, backoff, jitter) are all part of the same resiliency family but I'd still like them broken out individually rather than one mega doc, since each one has its own specific numbers/tuning that's worth calling out on its own.

Epxlain why a technical design should leverage something like Windows background job vs Azure Functions? Basically trying to understand when a long running worker process makes more sense than going serverless for our kind of background work.

for obserbvability use Loki, Grafana and OTEL - I'd rather lean on this open source combo than something like the Elastic stack for observability specifically, even though we're already using Elasticsearch elsewhere for analytics. Keep those two concerns separate in your head.


Security
Reputation issues and hacking issues and athrorization issues etc. Basically go beyond the baseline security stuff we already covered earlier and think about what changes once we're operating at real scale - things like our own domain getting flagged for abuse, people trying to scrape/enumerate the whole keyspace, and whether our authorization checks actually hold up once the data is spread across more than one store.

Also create a document on how ai can be plugged ito the app - making the design flixible for ai to operate. metadata and urls can be optionally represented as vectors at a leter proint in time. I'm not asking you to build any specific ai feature right now, just want the architecture to not paint us into a corner if we want to bolt something like that on down the road.

Batching:
can you add batching logic for url fetches. Multi inserts into Elastic Search - two different batching ideas here, one is around how we record fetch/click stats efficiently instead of writing on every single hit, and the other is just the standard "don't index into Elasticsearch one document at a time" advice, since doing that at our volume would be painful.

Infrstaructure design:
Use CDN, Firewall, Load Balancer and worker computers and use Kubernetis elastic model from azure so server capacity will increase automatically. Create an infrastrcuture design for this. I want to actually see this drawn out end to end, edge to data tier, not just a list of buzzwords - and explain how the autoscaling actually decides when to add more capacity.

Dev Ops; 
Create a document and say it is out of scope. Not because it doesn't matter, just because it's a whole separate review on its own and I don't want it diluting this one.


ShortKey generation :

There are several ways the short key can be generated. Each approavch has its own pros and cons. Please create a document and explain each one and compare the pros and cons of each one - I want to see the actual math behind collision risk where relevant, not just a vibes based comparison.


Saga Pattern:
using a bg agent explain why Saga agent in an overkill for us - as the creation process is not business critical. add it as a design document

ID Block Allocation, Domain Events , ignore Azure Service Bus

BFF Pattern 
CDN Edge Cache 
Redis Caching , Cache Invalidation 
outo of scope - QR Codes 
Blob Storage compare it with Cloudeflare R2 or B2 blackbase for cost
database partitioning / sharding
Idempotency -  Deduplication 

Sliding Windw Counter Distributed Rate Limiting can be better?
OpenTelemetry Distributed
Timeout  Retry 
Budget Transient Failures 
Exponential Backoff Retry Storm 
Jitter 
Circuit Breaker and Bulkhead Isolation 
Azure Functions
Short-Key Gen - snoflake