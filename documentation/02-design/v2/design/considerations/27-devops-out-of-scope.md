# Consideration 27 — DevOps: Out of Scope for This Review

**Version:** v2 (scalability review)
**Status:** Scope declaration — not a design document
**Traceability:** `prompt@review-desig.md` (review scope item: "Dev Ops: Create a document and say it is out of scope").

---

## 1. What Is Excluded, and Why

`prompt@review-desig.md` states its own scope explicitly: *"the review of a project like this can take a lot of questions and numerous days... I am limiting the scope of the review to one or two review items... Scope of the review: Scalability."*

DevOps — how software is built, tested, and delivered into environments — is a separate concern from application/system scalability architecture. Folding it into this review would dilute a review that is meant to stay scoped and finishable rather than open-ended. Accordingly, **DevOps is explicitly out of scope for this v2 review** and is not designed here or elsewhere in this document set.

## 2. What Would Fall Under a DevOps Review (Not Designed Here)

If a DevOps review is conducted later, it would cover items such as:

- CI/CD pipeline design (build, test, and quality gates per stage)
- Deployment automation and release management (blue/green, canary, etc.)
- Infrastructure-as-Code implementation (e.g., Bicep/Terraform authoring and workflow)
- Environment promotion strategy (dev → staging → production)
- Secrets and configuration management tooling (e.g., Key Vault integration, pipeline-level config)
- Rollback procedures

None of these are analyzed, designed, or recommended in this document. This is a bounded exclusion list, not a preview of the answers.

## 3. Adjacent but Distinct: Infrastructure Topology

`26-infrastructure-design.md` (infrastructure topology — CDN, firewall, load balancer, worker compute, and the Kubernetes elastic scaling model) is adjacent to DevOps but answers a different question:

- **`26-infrastructure-design.md`** answers *what* infrastructure exists and *where* it runs (topology).
- **DevOps** (this exclusion) answers *how* that infrastructure gets built, deployed, and updated over time (delivery process).

A reader should not treat `26-infrastructure-design.md` as a DevOps design — it is not one, and this document does not retroactively turn it into one.

## 4. Recommendation

Treat DevOps as its own separate, scoped review at a later time, consistent with `prompt@review-desig.md`'s own stated pattern of limiting each review pass to one or two items rather than expanding a single review indefinitely.
