import { defineConfig } from 'vitepress'

const GITHUB = 'https://github.com/SquareZero-Inc/bibim-revit'

// NOTE: `base` is set for GitHub *project* Pages
// (https://squarezero-inc.github.io/bibim-revit/). If this ever moves to a
// custom domain (e.g. docs.bibim.app) or org/user Pages, change `base` to '/'.
export default defineConfig({
  base: '/bibim-revit/',
  title: 'BIBIM AI',
  description: 'Natural language → executable C# inside Revit. User manual for the BIBIM AI Revit add-in.',
  cleanUrls: true,
  lastUpdated: true,
  head: [['link', { rel: 'icon', href: '/bibim-revit/favicon.ico' }]],

  themeConfig: {
    socialLinks: [{ icon: 'github', link: GITHUB }],
  },

  locales: {
    root: {
      label: 'English',
      lang: 'en-US',
      themeConfig: {
        nav: [
          { text: 'Guide', link: '/guide/getting-started' },
          { text: 'Releases', link: GITHUB + '/releases' },
        ],
        sidebar: {
          '/guide/': [
            {
              text: 'Guide',
              items: [
                { text: 'Getting started', link: '/guide/getting-started' },
                { text: 'Run with a local model (no API key)', link: '/guide/local-llm' },
              ],
            },
          ],
        },
        editLink: {
          pattern: GITHUB + '/edit/main/docs/:path',
          text: 'Edit this page on GitHub',
        },
      },
    },

    ko: {
      label: '한국어',
      lang: 'ko-KR',
      link: '/ko/',
      themeConfig: {
        nav: [
          { text: '가이드', link: '/ko/guide/getting-started' },
          { text: '릴리스', link: GITHUB + '/releases' },
        ],
        sidebar: {
          '/ko/guide/': [
            {
              text: '가이드',
              items: [
                { text: '시작하기', link: '/ko/guide/getting-started' },
                { text: '로컬 모델로 실행 (API 키 없이)', link: '/ko/guide/local-llm' },
              ],
            },
          ],
        },
        editLink: {
          pattern: GITHUB + '/edit/main/docs/:path',
          text: 'GitHub에서 이 페이지 편집',
        },
        docFooter: { prev: '이전', next: '다음' },
      },
    },
  },
})
