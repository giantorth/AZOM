module.exports = function (eleventyConfig) {
  // Only treat Nunjucks + Markdown as templates. The bespoke landing page
  // (site/index.html) is copied verbatim via passthrough — never processed.
  eleventyConfig.setTemplateFormats(["njk", "md"]);

  // Verbatim files emitted at the site root (input-dir prefix is stripped).
  eleventyConfig.addPassthroughCopy("site/index.html");
  eleventyConfig.addPassthroughCopy("site/styles.css");
  eleventyConfig.addPassthroughCopy("site/favicon.svg");
  eleventyConfig.addPassthroughCopy("site/CNAME");

  // Marketing images live in repo docs/ (shared with the GitHub README). Publish
  // only image files — internal docs/ markdown stays unpublished. Path is preserved
  // so the pages can reference /docs/<image>.
  eleventyConfig.addPassthroughCopy("docs/**/*.{png,webp,jpg,jpeg,gif,svg}");

  // Guides collection, ordered by front-matter `order`.
  eleventyConfig.addCollection("guides", (collectionApi) =>
    collectionApi
      .getFilteredByTag("guide")
      .sort((a, b) => (a.data.order || 0) - (b.data.order || 0))
  );

  return {
    dir: { input: "site", includes: "_includes", output: "_site" },
    markdownTemplateEngine: "njk",
    htmlTemplateEngine: "njk",
  };
};
