# Markdown Style Guide Checklist

> **Scope:** Markdown files in docs, engineering checklists, ADRs, persona docs, READMEs, runbooks, release notes, GitHub templates, and support content.
> **Priority:** SUPPLEMENTARY.
> **Owners:** technical-writer, developer-experience-api-engineer. **Grounded in:** `.github/copilot-instructions.md`, 11, technical-writer persona docs, accessible documentation and developer style guidance.

## How to use
Use this checklist for Markdown-only changes and as the style layer for 11. Prefer consistency, accessibility, linkability, and reviewability over local formatting preferences.

## Checklist
### Document structure
- [ ] Each page has exactly one H1 that matches the file purpose and is not repeated later.
- [ ] Heading levels increase one level at a time without skipping from H2 to H4.
- [ ] Headings use sentence case unless an official product name, API name, acronym, or checklist title requires otherwise.
- [ ] Sections are short enough to scan and have headings that describe the reader task or concept.
- [ ] Front matter, if used, follows the repository convention for keys, order, and quoting.
- [ ] The document can be understood from top to bottom without relying on hidden context from the PR.

### Lists, tasks, and emphasis
- [ ] Bulleted lists use `-` consistently, with parallel grammar where practical.
- [ ] Ordered lists are used only when order matters.
- [ ] Checklist items use `- [ ]` or `- [x]` consistently and are phrased as verifiable checks.
- [ ] Nested lists are indented consistently and are not deeper than necessary.
- [ ] Bold text emphasizes labels or critical phrases; it is not used as a substitute for headings.
- [ ] Italics are used sparingly and not for product names or code identifiers.

### Code, commands, and API names
- [ ] Fenced code blocks specify a language such as `csharp`, `bash`, `yaml`, `json`, `sql`, `text`, or `markdown`.
- [ ] Inline code formatting is used for API names, filenames, paths, commands, configuration keys, CRD fields, metric names, and literal values.
- [ ] Commands are copyable, avoid shell prompts unless the prompt is being explained, and include expected output when helpful.
- [ ] Code snippets compile or are explicitly marked as conceptual or abbreviated.
- [ ] Long code blocks are introduced with context and followed by explanation or verification when needed.
- [ ] Examples do not include secrets, real credentials, tenant identifiers, or object-store keys.

### Links and references
- [ ] Relative links are used for repository-local files and resolve from the current file location.
- [ ] Link text is descriptive; avoid “here,” “this,” or bare URLs when meaningful text is possible.
- [ ] Anchors are stable and checked after heading changes.
- [ ] External links point to authoritative sources and include enough surrounding text to explain why they matter.
- [ ] Cross-links use checklist numbers where relevant, such as 09a, 09b, 09c, 11, 14, or markdown-style-guide.
- [ ] Reference lists are deduplicated and do not include unused or unreachable links.

### Tables and diagrams
- [ ] Tables have a header row, separator row, consistent column count, and concise cell content.
- [ ] Tables are used for comparison or matrix content, not for long prose that is easier to read as lists.
- [ ] Wide tables are avoided or split when they become hard to read in a terminal or code review.
- [ ] Diagram source is committed or the diagram is reproducible from text where practical.
- [ ] Images include meaningful alt text and adjacent explanation for essential information.
- [ ] Screenshots are avoided for text, commands, errors, or diagrams that can be represented accessibly.

### Line length, wrapping, and whitespace
- [ ] Prose is wrapped consistently for readable diffs, preferably around 80-100 characters unless tables, links, or code are clearer unwrapped.
- [ ] Code blocks preserve required formatting and are not hard-wrapped in a way that breaks commands or examples.
- [ ] Files end with a single trailing newline.
- [ ] Lines do not contain trailing whitespace.
- [ ] Blank lines separate headings, paragraphs, lists, code fences, blockquotes, and tables consistently.
- [ ] Tabs are not used for Markdown indentation unless required by an embedded format.

### Terminology and style
- [ ] Product and technology names are spelled consistently: DeltaSharp, Delta, Delta Lake, Parquet, Apache Spark, Spark, Kubernetes, .NET, gRPC, Arrow Flight, OpenTelemetry.
- [ ] Use “driver,” “executor,” “stage,” “task,” “shuffle,” “Operator,” and “CRD” consistently with project architecture docs.
- [ ] Prefer direct, active voice and globally readable sentences.
- [ ] Distinguish public contract, implementation detail, preview behavior, and future roadmap.
- [ ] Avoid unsupported claims such as “simple,” “obvious,” “just,” “best,” or “guaranteed” unless the claim is verified and useful.
- [ ] Security, privacy, reliability, and performance claims link to the owning checklist, ADR, benchmark, or runbook.

### Admonitions and blockquotes
- [ ] Blockquotes are used for notes, scope, compatibility warnings, or quoted material, not for ordinary body text.
- [ ] Warnings state the risk, affected users, and safe action.
- [ ] Notes add decision-relevant information instead of repeating the surrounding paragraph.
- [ ] Long warnings or prerequisites are promoted to their own heading when they become multi-step guidance.
- [ ] Blockquote styling remains plain Markdown so it renders acceptably on GitHub and in terminal views.

### Validation and review
- [ ] Markdown renders correctly on GitHub and in the project documentation site when one exists.
- [ ] Link checks, markdown linting, spelling, or formatter checks pass where configured.
- [ ] Changes to checklist format still match `docs/engineering/checklists/README.md`.
- [ ] Documentation changes that affect public APIs, runbooks, examples, or releases are reviewed with 11.
- [ ] The diff avoids unrelated reflow that obscures the substantive change.

## Anti-patterns (red flags)
- Multiple H1 headings, skipped heading levels, or headings used only for visual size.
- Unlabeled code fences or code snippets that cannot be copied safely.
- Broken relative links, vague link text, stale anchors, or raw URLs used as prose.
- Tables with mismatched columns, paragraphs inside cells, or inaccessible screenshot substitutes.
- Trailing whitespace, inconsistent list markers, missing final newline, or noisy unrelated rewrapping.
- Product-name drift such as “Delta Sharp,” “delta sharp,” “Spark.NET,” or inconsistent Delta and Parquet casing.
- Warnings that sound serious but do not tell the reader what risk to avoid or what action to take.

## References
- [11 — Documentation Support Checklist](11-documentation-support-checklist.md)
- `docs/engineering/checklists/README.md`
- `.github/copilot-instructions.md`
- `docs/persona/agents/technical-writer-agent.md`
- `docs/persona/research/technical-writer.md`
- GitHub Flavored Markdown specification
- Microsoft Writing Style Guide
- Google developer documentation style and accessibility guidance
- Write the Docs docs-as-code guidance
