# PageRank-Crawler

Web scraper using Chrome via Puppeteer to crawl a website and create a multi-graph containing nodes for each page and keyword of relevance, and links between pages and page-keywords.
The goal of the project is to create a tool to evaluate the structure of a website and evaluate how appropriate the structure is to the intent of the site.

## Neo4j
It currently uses Neo4j 3.5 due to incompatibilities with the Neo4jClient library
APOC and Graph Data Science Library plugins need to be installed for analysis

### Example / useful queries

Export all data into a csv

    CALL apoc.export.csv.all("first.csv", {})


Get stats on what's in the database

    CALL apoc.meta.stats()
    YIELD nodeCount, relCount, labels, relTypesCount
    RETURN nodeCount, relCount, labels, relTypesCount;

Get a few Page -> Keyword relationships to see what they look like

    MATCH (n)-[:KEYUSED]->(s) RETURN n,s limit 50
    
![page to keyword relationships in neo4j](/Pages-to-keywords.png "Page to Keyword Relationships")

Get a list of pages and count of inbound links

    MATCH ()-[l:LINKS]->(p:Page) RETURN p.Url,COUNT(l) 

Get a list of pages and count of outbound links

    MATCH (p:Page)-[l:LINKS]->() RETURN p.Url,COUNT(l) 

Get a list of keywords, total score and number of pages they're found on

    MATCH (p:Page)-[keyw:KEYUSED]->(k:Keyword) RETURN k.Keyword,SUM(keyw.Score),COUNT(p)

Get a list of keywords found in alt attributes of images, total score and number of pages they're found on

    MATCH (p:Page)-[keyw:KEYUSED{Type:'img'}]->(k:Keyword) RETURN k.Keyword,SUM(keyw.Score),COUNT(p) 

Get a list of keywords found in anchor tags, total score and number of pages they're found on

    MATCH (p:Page)-[keyw:KEYUSED{Type:'a'}]->(k:Keyword) RETURN k.Keyword,SUM(keyw.Score),COUNT(p) 

### Calculate page rank

Create a named graph called pagerank out of our Page nodes and LINKS relationships

    CALL gds.graph.create(
      'pagerank',
      'Page',
      'LINKS',
      {
    
      }
    )

Call the pageRank function in gds to calculate the pagerank 

    CALL gds.pageRank.stream('pagerank')
    YIELD nodeId, score
    RETURN gds.util.asNode(nodeId).Url AS name, score
    ORDER BY score DESC, name ASC

## Use cases so far
The crawler has led me to understand how I was accidentally creating 20*40! (20 times 40 factorial) number of pages via links while I was just trying to filter some stuff on a few pages.. thus search engines were scraping the same thing to exhaustion instead of scraping the valuable content on the site
