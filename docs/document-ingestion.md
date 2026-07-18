# Document extraction, chunking, and local embeddings

`SlimVector.DocIngestor` is a storage-independent .NET 10 library. It turns a PDF, Word document, PowerPoint presentation, text file, or Markdown file into structured text sections, overlapping chunks, and normalized dense vectors. It can be reused without the Studio and does not depend on `SlimVector.Application`.

## Supported formats

| Format | Extractor | Provenance retained |
| --- | --- | --- |
| PDF | PdfPig content-order extraction | page number and `Page N` location |
| DOCX | DocumentFormat.OpenXml | headings and logical sections |
| PPTX | DocumentFormat.OpenXml | slide number and first text line as heading |
| TXT / Markdown | UTF-8 plain text | document location |

The project pins `PdfPig.Rendering.Skia` 0.1.14.2 and `SkiaSharp` 3.119.4 alongside PdfPig, matching the rendering stack used by the GnOuGo reference. Text extraction itself does not rasterize pages. A PDF containing only scanned images returns the stable `document_contains_no_text` error; OCR is deliberately not guessed or sent to a cloud service.

DOCX and PPTX are Open XML formats. Legacy `.doc` and `.ppt` binaries are not accepted and should first be converted to their Open XML equivalents.

## Chunking

Three strategies share the same size policy:

- `Recursive` tries paragraph, newline, sentence, punctuation, and word boundaries in that order.
- `Paragraph` favors paragraph and newline boundaries.
- `Sentence` favors sentence punctuation.

Each `TextChunk` contains its stable sequence, text, estimated token count, contributing section numbers, distinct source locations, and nearest heading. The chunker:

- never emits a normal chunk above `MaximumTokens`;
- carries a configurable tail into the next chunk as overlap;
- merges a very small final chunk when the merged result remains bounded;
- produces deterministic output for identical text and options.

The defaults target 90 estimated tokens, cap at 120, and overlap by 18. These values leave room for the embedding model's 128-token sequence limit. The ONNX generator also performs a final safe truncation and preserves the last special token.

## Embedding model

The default model is [`sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2`](https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2), pinned to an immutable Hugging Face revision. It is Apache-2.0 licensed, supports 50 languages, and maps sentences or paragraphs to 384 dimensions.

The library downloads only two files on first use: `tokenizer.json` and one ONNX graph. Selection is automatic:

| Process architecture | ONNX artifact |
| --- | --- |
| ARM64, including Apple Silicon | `model_qint8_arm64.onnx` |
| x64 with AVX2 | `model_quint8_avx2.onnx` |
| other supported CPU | `model.onnx` portable fallback |

Tokenization uses the native Hugging Face tokenizer bindings for Windows x64/ARM64, Linux x64/ARM64, and macOS x64/ARM64. ONNX Runtime performs batched CPU inference. The implementation applies attention-mask-aware mean pooling to `last_hidden_state` and L2-normalizes every vector, so cosine and dot-product behavior is stable.

Downloads use an immutable revision URL and an atomic temporary file. A partial download is removed and never treated as a ready model. Set `AutoDownload = false` to enforce a pre-provisioned offline cache.

## Dependency injection

```csharp
using SlimVector.DocIngestor;
using SlimVector.DocIngestor.Abstractions;
using SlimVector.DocIngestor.Models;

services.AddSlimVectorDocIngestor(options =>
{
    options.ModelDirectory = "/var/lib/my-app/models/minilm";
    options.AutoDownload = true;
    options.BatchSize = 8;
});

IDocumentIngestionPipeline pipeline = provider.GetRequiredService<IDocumentIngestionPipeline>();
await using DocumentSource source = DocumentSource.FromFile("architecture.pdf");
IngestionResult result = await pipeline.IngestAsync(source, new IngestionOptions
{
    Chunking = new ChunkingOptions
    {
        Strategy = ChunkingStrategy.Recursive,
        TargetTokens = 90,
        MaximumTokens = 120,
        OverlapTokens = 18,
    },
});

foreach (EmbeddedChunk chunk in result.Chunks)
{
    Console.WriteLine($"{chunk.Id}: {chunk.Vector.Length} dimensions, {chunk.Chunk.Locations[0]}");
}
```

`IngestionResult` also exposes the SHA-256 content hash, deterministic document identifier, extracted document metadata, and extraction/chunking/embedding durations. Set `GenerateEmbeddings = false` for a text-only preview; vectors in that result are empty arrays.

## Abstractions

- `IDocumentTextExtractor` adds a new format without changing the router.
- `ITokenCounter` and `ITextChunker` allow a different sizing policy.
- `IEmbeddingGenerator` allows a different local or remote embedding implementation.
- `IDocumentIngestionPipeline` is the single orchestration entry point.

Stable `DocumentIngestionException.Code` values let a host map failures to its own HTTP or UI contract.

## Operational notes

- Treat uploaded Open XML/PDF content as untrusted and keep host upload limits enabled.
- A 384-dimension collection is required when using the default model with SlimVector.
- The full portable ONNX fallback is larger than the quantized architecture-specific variants.
- Model initialization is lazy; extraction-only application startup does not require Hugging Face connectivity.
- The Studio's default cache is outside the repository under the current user's local application-data folder.
