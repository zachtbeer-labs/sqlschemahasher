// @ts-check
// Docusaurus configuration for the SqlSchemaHasher documentation site.
// See https://docusaurus.io/docs/api/docusaurus-config

const { themes } = require('prism-react-renderer');

/** @type {import('@docusaurus/types').Config} */
const config = {
  title: 'SqlSchemaHasher',
  tagline: 'One deterministic SHA256 per SQL Server database schema — ground truth for drift detection across fleets.',
  favicon: 'img/icon.png',

  // Production site URL and the base path it is served under.
  // Project site: https://zachtbeer-labs.github.io/sqlschemahasher/
  url: 'https://zachtbeer-labs.github.io',
  baseUrl: '/sqlschemahasher/',

  organizationName: 'zachtbeer-labs',
  projectName: 'sqlschemahasher',

  // Fail the build on any dead internal link so content migration stays honest.
  onBrokenLinks: 'throw',

  markdown: {
    hooks: {
      onBrokenMarkdownLinks: 'throw',
    },
  },

  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },

  presets: [
    [
      'classic',
      /** @type {import('@docusaurus/preset-classic').Options} */
      ({
        docs: {
          // Serve the docs at the site root; there is no separate landing page yet.
          routeBasePath: '/',
          sidebarPath: require.resolve('./sidebars.js'),
          editUrl: 'https://github.com/zachtbeer-labs/sqlschemahasher/edit/main/website/',
        },
        blog: false,
        theme: {
          customCss: require.resolve('./src/css/custom.css'),
        },
      }),
    ],
  ],

  themeConfig:
    /** @type {import('@docusaurus/preset-classic').ThemeConfig} */
    ({
      image: 'img/icon.png',
      colorMode: {
        defaultMode: 'dark',
        respectPrefersColorScheme: true,
      },
      navbar: {
        title: 'SqlSchemaHasher',
        logo: {
          alt: 'SqlSchemaHasher',
          src: 'img/icon.png',
        },
        items: [
          {
            type: 'docSidebar',
            sidebarId: 'docsSidebar',
            position: 'left',
            label: 'Docs',
          },
          {
            href: 'https://www.nuget.org/packages/zachtbeer.SqlSchemaHasher',
            label: 'NuGet',
            position: 'right',
          },
          {
            href: 'https://github.com/zachtbeer-labs/sqlschemahasher',
            label: 'GitHub',
            position: 'right',
          },
        ],
      },
      footer: {
        style: 'dark',
        links: [
          {
            title: 'Docs',
            items: [
              { label: 'Getting Started', to: '/getting-started' },
              { label: 'What Gets Hashed', to: '/what-gets-hashed' },
              { label: 'Options & Presets', to: '/options-and-presets' },
              { label: 'FAQ', to: '/faq' },
            ],
          },
          {
            title: 'Project',
            items: [
              { label: 'GitHub', href: 'https://github.com/zachtbeer-labs/sqlschemahasher' },
              { label: 'NuGet', href: 'https://www.nuget.org/packages/zachtbeer.SqlSchemaHasher' },
              { label: 'Changelog', href: 'https://github.com/zachtbeer-labs/sqlschemahasher/blob/main/CHANGELOG.md' },
            ],
          },
        ],
        copyright: `Copyright © ${new Date().getFullYear()} Zachtbeer Labs B.V. Licensed under MIT.`,
      },
      prism: {
        theme: themes.github,
        darkTheme: themes.dracula,
        additionalLanguages: ['csharp', 'sql', 'bash'],
      },
    }),
};

module.exports = config;
