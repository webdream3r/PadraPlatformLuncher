namespace Padra.Core.Configuration;

/// <summary>The `models.yaml` written on a first run, next to the application.</summary>
public static class StarterConfig
{
    public const string Yaml = @"# Padra Platform Launcher — model fleet
#
# The control panel writes this file when you add, remove or reconfigure a
# model, and it stays hand-editable. Every entry under `models:` becomes a
# selectable model id at the gateway; the gateway starts `unsloth run` for it
# on demand and routes by the `model` field of each request.
#
# On first start the launcher fills `models:` from the GGUF models already
# downloaded on this machine (the Hugging Face cache and LM Studio). Use the
# ""Model library"" panel to add more from the Unsloth catalog.

gateway:
  # 127.0.0.1 keeps the gateway private. Turn on ""Share with the local network""
  # in the control panel (or set 0.0.0.0 here) to serve other machines.
  host: 127.0.0.1
  port: 8900

  # Key every client must send. Left empty, a strong key is generated on the
  # first run and written back here. Env override: GATEWAY_API_KEY
  apiKey: ''

  # exclusive  -> one backend loaded at a time; swaps on demand (single GPU)
  # concurrent -> all backends stay loaded (needs VRAM for all)
  mode: exclusive

  # Used when a request omits ""model"". Filled in when the first model is added.
  defaultModel: ''

  # Start default_model at boot instead of on first request.
  preload: false

  # Seconds of inactivity before a loaded model is unloaded. 0 disables it.
  idleTimeout: 0

  # Seconds. Agentic turns are long; keep this generous.
  requestTimeout: 1800

  # Seconds to wait for `unsloth run` to load a model.
  startTimeout: 900

  # Path to the unsloth CLI.
  unslothBin: unsloth

  # Wire format written into ~/.codex/config.toml. Codex documents ""responses""
  # as the supported value; ""chat"" is available for older builds.
  wireApi: responses

  # `default` serves unlisted model ids with the default model, which is what
  # Claude Code needs. `error` returns 404 instead.
  unknownModel: default

  # Fill an empty fleet from the models already on this disk.
  autoImport: true

  # Which server the coding agents are configured to use:
  #   gateway -> this launcher, one endpoint in front of every model below
  #   unsloth -> Unsloth's own server (Unsloth Studio or `unsloth run`), reached
  #              directly; it may run on another machine on the network
  endpointSource: gateway

  # That server's origin. Empty means http://127.0.0.1:8888, or whatever
  # UNSLOTH_STUDIO_URL names. A server on another machine: http://192.168.1.20:8888
  unslothServerUrl: ''

  # A key for it. Left empty, the key `unsloth start` cached on this machine is
  # used; the control panel can also sign in to the server and create one.
  unslothApiKey: ''

  # The model the agents are pointed at on that server, as it names it
  # (org/repo or org/repo:QUANT). Empty = whatever model is resident.
  unslothModel: ''

  # Extra folders to scan for GGUF files, on top of the Hugging Face cache.
  extraModelPaths: []

models: []

# Each entry looks like this:
#
#   - alias: qwen3-coder-30b
#     repo: unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF
#     quant: UD-Q4_K_XL
#     port: 8801
#     contextLength: 262144
#     reasoning: off          # on | off, for reasoning-capable models
#     disableTools: true     # required when an external coding agent runs the tools
#     extraArgs: []          # passed straight to `unsloth run`
#     env: {}                 # e.g. { CUDA_VISIBLE_DEVICES: ""0"" }
#     # apiKey: sk-unsloth-… # skip capturing the key from `unsloth run` output
#     # servedId: …          # skip /v1/models discovery
#
# A model larger than this machine's graphics memory still runs: the gateway asks
# the inference server to keep part of it in system memory. The ""Hardware and
# offload"" panel writes the block below, and every key is optional.
#
#     runtime:
#       gpuMemoryMode: auto   # auto | manual
#       gpuLayers: 24         # layers kept in VRAM; -1 = all
#       fit: on               # auto | on | off — shrink and offload until it fits
#       fitContext: 8192      # smallest context `fit` may fall back to
#       cpuMoeLayers: 20      # expert layers held in system RAM; -1 = all of them
#       kvCacheType: q8_0     # f32 f16 bf16 q8_0 q5_1 q5_0 iq4_nl q4_1 q4_0
#       flashAttention: on    # auto | on | off
#       threads: 12
#       noKvOffload: false    # keep the KV cache in system RAM
#       swaFull: false        # allocate the full sliding-window cache
#       allowOversize: true   # start models this machine cannot hold in VRAM
";
}
