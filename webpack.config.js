const path = require("path");
const distPath = path.resolve(__dirname, "public");

const CopyPlugin = require("copy-webpack-plugin");
const ConcatPlugin = require("@mcler/webpack-concat-plugin");
const HtmlWebpackPlugin=require("html-webpack-plugin");

const vendorLibs = [
  "jquery-1.11.3.min.js",
  "bootstrap.min.js",
  "jquery.stellar.min.js",
  "jquery.scrollTo.min.js",
  "jquery.localScroll.min.js",
  "owl.carousel.min.js",
  "jquery.nav.js",
  "wow.min.js",
  "jquery.nicescroll.min.js",
  "matchMedia.js",
  "navbar.matchMedia.js",
  "jquery.ajaxchimp.min.js",
  "jquery.countdown.js",
  "fancySelect.js",
  "smoothscroll.js",
  /*"https://maps.google.com/maps/api/js?sensor=false"*/
];

const copyDirs = ["css", "images", "fonts"];

module.exports = {
  entry: "./src/index.js",
  output: {
    filename: "main.js",
    path: distPath
  },
  module: {
    rules: [
        {
          test: /\.hbs$/,
          loader: 'handlebars-loader'
        },
      ],
    noParse: (content) => /vendor-libs/.test(content),
  },
  devServer: {
    static: {
      directory: distPath,
    },
    compress: true,
    port: 9000,
  },
  plugins: [
    new CopyPlugin({
      patterns: copyDirs.map((dir) => {
        return { from: path.join("src", dir), to: path.join(distPath, dir) };
      }),
    }),
    new ConcatPlugin({
      name: "vendor",
      fileName: "[name].[hash:8].js",
      filesToConcat: vendorLibs.map((lib) => `./src/vendor-libs/${lib}`),
      attributes: {
        async: true,
      },
    }),
    new HtmlWebpackPlugin({
        template: './src/index.hbs'
      })
  ],
};
