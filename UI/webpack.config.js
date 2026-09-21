const path = require("path");
const MOD = require("./mod.json");
const MiniCssExtractPlugin = require("mini-css-extract-plugin");
const TerserPlugin = require("terser-webpack-plugin");
const { CSSPresencePlugin } = require("./tools/css-presence");

// Always build locally. The C# project deploys this directory after DeployWIP;
// writing into the live mod directory here would be erased moments later by
// the toolchain's clean deployment step.
const outputRoot = path.resolve(__dirname, "build");

const banner = `
 * Cities: Skylines II UI Module
 *
 * Id: ${MOD.id}
 * Author: ${MOD.author}
 * Version: ${MOD.version}
 * Dependencies: ${MOD.dependencies.join(",")}
`;

module.exports = {
  mode: "production",
  entry: { [MOD.id]: "./src/index.tsx" },
  externalsType: "window",
  externals: {
    react: "React",
    "react-dom": "ReactDOM",
    "cs2/modding": "cs2/modding",
    "cs2/api": "cs2/api",
    "cs2/ui": "cs2/ui"
  },
  module: {
    rules: [
      {
        test: /\.tsx?$/,
        use: "ts-loader",
        exclude: /node_modules/
      },
      {
        test: /\.less$/,
        include: path.join(__dirname, "src"),
        use: [
          MiniCssExtractPlugin.loader,
          {
            loader: "css-loader",
            options: {
              modules: {
                auto: true,
                exportLocalsConvention: "camelCase",
                localIdentName: "[local]_[hash:base64:3]"
              }
            }
          },
          "less-loader"
        ]
      },
      {
        test: /\.(png|jpe?g|gif|svg)$/i,
        type: "asset/resource",
        generator: { filename: "images/[name][ext][query]" }
      }
    ]
  },
  resolve: {
    extensions: [".tsx", ".ts", ".js"],
    modules: ["node_modules", path.join(__dirname, "src")]
  },
  output: {
    path: outputRoot,
    library: { type: "module" },
    publicPath: "coui://ui-mods/"
  },
  optimization: {
    minimize: true,
    minimizer: [new TerserPlugin({
      extractComments: { banner: () => banner }
    })]
  },
  experiments: { outputModule: true },
  plugins: [new MiniCssExtractPlugin(), new CSSPresencePlugin()]
};
