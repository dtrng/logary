const express = require('express')
const next = require('next')
const { parse } = require('url')
const routes = require("./routes")

const dev = process.env.NODE_ENV !== 'production'
const app = next({ dev })
const handle = app.getRequestHandler()

const remap = actualPath => {
  if (routes.hasOwnProperty(actualPath)) {
    console.log(`Mapping ${actualPath} => ${routes[actualPath]}`)
    return [ true, routes[actualPath] ]
  } else {
    return [ false, null  ];
  }
}

app
  .prepare()
  .then(() => {
    const server = express()
    const port = process.env.PORT || 3000

    server.get('*', (req, res) => {
      const parsedUrl = parse(req.url, true)
      const { pathname, query } = parsedUrl
      const [ shouldRemap, newPath ] = remap(pathname);
      if (shouldRemap) {
        return app.render(req, res, newPath, query)
      } else {
        // console.log('got parsed url ', parsedUrl)
        return handle(req, res, parsedUrl);
      }
    })

    server.listen(port, err => {
      if (err) throw err
      console.info(`> Ready on http://localhost:${port}`)
    })
  })
  .catch(ex => {
    console.error(ex.stack)
    process.exit(1)
  })