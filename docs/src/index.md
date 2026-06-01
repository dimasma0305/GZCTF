---
pageType: home
titleSuffix: "GZ::CTF A&D Docs"

hero:
  name: "GZ::CTF A&D"
  text: |
    Attack & Defense
    King of the Hill
  tagline: A jeopardy + A&D + KotH fork of GZ::CTF — rounds, live checkers, per-team containers and hold scoring.
  actions:
    - theme: brand
      text: Learn More
      link: /guide/start/introduction
    - theme: alt
      text: Quick Start
      link: /guide/start/quick-start

features:
  - title: Attack & Defense engine
    details: Tick-based rounds, per-team service containers, rotating /flag, an enochecker3 SLA checker and WireGuard VPN access — all on the AdEngine.
    icon: ⚔️
  - title: King of the Hill
    details: One shared hill the whole game fights to control. Plant your game-wide control token in /koth/king; hold a healthy hill to earn points.
    icon: 👑
  - title: Jeopardy too
    details: Everything upstream GZ::CTF does — dynamic scoring, dynamic containers, scoreboards, cheat detection — still works alongside the A&D engine.
    icon: 🚩
  - title: Author from a repo
    details: Point a repo binding at the TCP1PADTesting example repo and the server clones it, globs its .gzevent, and auto-builds every service + checker. Re-scans on the interval you set.
    icon: 📦
  - title: Docker or Kubernetes
    details: Stand the platform up with the wizard + make platform-up, then set ContainerProvider in appsettings.json to Docker or Kubernetes. Per-team containers, egress isolation, resource limits and snapshot capture.
    icon: 🐳
  - title: Open source
    details: Built on GZ::CTF (AGPLv3). The A&D / KotH engine, scoring and authoring pipeline are documented here.
    icon: 📖
---
