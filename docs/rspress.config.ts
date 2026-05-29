import { defineConfig } from "@rspress/core";

// GitHub Pages project site for dimasma0305/GZCTF is served under /GZCTF/.
// Override PUBLIC_URL / base if you use a custom domain or a user/org page.
const PUBLIC_URL = process.env.PUBLIC_URL || "https://dimasma0305.github.io/GZCTF";

export default defineConfig({
  // The markdown lives in docs/src so this Rspress project can sit inside the
  // platform repo's docs/ folder without an awkward docs/docs nesting.
  root: "src",
  base: "/GZCTF/",
  outDir: "doc_build",
  title: "GZ::CTF A&D",
  lang: "en",
  description:
    "Attack & Defense and King of the Hill engine for GZ::CTF — setup, scoring, challenge authoring and deployment.",
  route: { cleanUrls: true },
  search: { codeBlocks: true },
  ssg: true,
  mediumZoom: true,
  themeConfig: {
    socialLinks: [
      {
        icon: "github",
        mode: "link",
        content: "https://github.com/dimasma0305/GZCTF",
      },
    ],
    lastUpdated: true,
    enableScrollToTop: true,
    footer: {
      message:
        "Attack & Defense / King of the Hill fork of GZ::CTF. Core © GZTimeWalker, AGPLv3.",
    },
  },
});
