# Introduction 
Web site for kagw Reel deal

# How does it work
the base of teh site is a bootstrap template I bought.
It uses really oudated js libs.
I kinda upgraded it to use webpack. 

The main index is handle bar template.

A helper called **data** is used to pull in sections of the site from the **/data/** folder.
sectiond are sored as markdown files.

## For example,
The tearms and rules secton is read from a file /data/terms-condition.md.

using

    {{{data "terms-condition.md"}}}

in index.hbs pulls /data/terms-condition.md and renders it inside the index html's tearms and ruls model.

The side is rendered to its final for at build buy running

    npm run build

It publishes the site to ./public folder.

Any push to main kicks of an Azure Dev Ops pipeline that build the site and deploys it using [Azure Static Web App](https://docs.microsoft.com/en-us/azure/static-web-apps/)

# Test
[reeldeal test site](https://black-grass-04c9be110.azurestaticapps.net/)
