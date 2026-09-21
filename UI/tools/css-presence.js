const { Compilation, sources } = require("webpack");

class CSSPresencePlugin {
  apply(compiler) {
    compiler.hooks.thisCompilation.tap("CSSPresencePlugin", (compilation) => {
      compilation.hooks.processAssets.tap(
        {
          name: "CSSPresencePlugin",
          stage: Compilation.PROCESS_ASSETS_STAGE_REPORT
        },
        (assets) => {
          const hasCSS = Object.keys(assets).some((name) => name.endsWith(".css"));
          const modules = Object.keys(assets).filter((name) => name.endsWith(".mjs"));

          if (modules.length === 0) {
            compilation.errors.push(new Error("CSSPresencePlugin: no .mjs output found"));
            return;
          }

          for (const moduleName of modules) {
            compilation.updateAsset(
              moduleName,
              new sources.ConcatSource(assets[moduleName], `\nexport const hasCSS=${hasCSS};\n`)
            );
          }
        }
      );
    });
  }
}

module.exports = { CSSPresencePlugin };
