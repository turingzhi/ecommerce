"""Verify public/catalog administration contracts with fresh operator fixtures."""

from support import check, compose, login, register_and_login, request, scalar
import time
from uuid import uuid4
from urllib.parse import quote

def main():
    marker=uuid4().hex;password="CatalogAdmin!123456";email=f"catalog-admin-{marker}@example.invalid"
    customer=register_and_login(f"catalog-customer-{marker}@example.invalid",password);old=register_and_login(email,password)
    request("GET","/admin/products",expected=401);request("GET","/admin/products",token=customer,expected=403)
    for _ in range(2):compose("exec","-T","ecommerce","dotnet","Ecommerce.Api.dll","--grant-product-admin",email)
    check(scalar(f"SELECT COUNT(*) FROM dbo.AspNetUserClaims c JOIN dbo.AspNetUsers u ON u.Id=c.UserId WHERE u.Email='{email}' AND c.ClaimValue='products:manage'")==1,"Duplicate catalog claim")
    request("GET","/admin/products",token=old,expected=403);admin=login(email,password)
    products=[]
    for name,price in [('A',3000),('B',1000),('C',1000)]:
        products.append(request("POST","/admin/products",token=admin,body={"name":marker+' '+name,"description":"Sorting fixture","category":"Catalog-"+marker,"priceCents":price,"available":2},expected=201))
    first=products[0];detail=request("GET",f"/products/{first['id']}");check(detail['available']==2 and detail['currency']=='EUR' and 'version' not in detail,"Public detail wrong")
    request("GET","/products/2147483647",expected=404)
    browse=request("GET","/products?category="+quote('Catalog-'+marker)+"&sort=priceAsc")
    expected=[products[1]['id'],products[2]['id'],products[0]['id']]
    check(browse['total']==3 and [p['id'] for p in browse['products']]==expected,"SQL browse sorting/tie order wrong")
    check(request("GET","/products?category="+quote('catalog-'+marker))['total']==0,"SQL category is case insensitive")
    for path in ['/products?page=0','/products?sort=relevance','/products/search?q='+marker+'&sort=invalid']:
        request("GET",path,expected=400)
    for _ in range(30):
        found=request("GET","/products/search?q="+marker+"&sort=priceAsc")
        if len(found['products'])==3:break
        time.sleep(1)
    check([p['id'] for p in found['products']]==expected,"Elastic priceAsc not sorted")
    descending=request("GET","/products/search?q="+marker+"&sort=priceDesc")
    check([p['id'] for p in descending['products']]==[products[0]['id'],products[1]['id'],products[2]['id']],"Elastic priceDesc tie order wrong")
    check(request("GET","/products/search?q="+marker+"&sort=priceAsc")==found,"Cached ascending response changed")
    check(request("GET","/products/search?q="+marker+"&sort=priceDesc")==descending,"Cached descending response changed")
    body={"name":marker+' Updated',"description":"Updated","category":"Catalog-"+marker,"priceCents":500,"expectedVersion":first['version']}
    request("PUT",f"/admin/products/{first['id']}",token=customer,body=body,expected=403)
    updated=request("PUT",f"/admin/products/{first['id']}",token=admin,body=body)
    check(updated['available']==2 and updated['version']>first['version'],"Admin update changed stock or failed version")
    request("PUT",f"/admin/products/{first['id']}",token=admin,body=body,expected=409)
    request("POST","/admin/products",token=admin,body={"name":None,"description":"","category":"A","priceCents":1,"available":1},expected=400)
    for _ in range(30):
        current=request("GET","/products/search?q="+marker+"&sort=priceAsc")
        if current['products'][0]['priceCents']==500:break
        time.sleep(1)
    check(current['products'][0]['id']==first['id'] and current['products'][0]['priceCents']==500,"Catalog edit did not invalidate sorted caches")
    check(request("GET","/admin/products?page=2147483647&pageSize=50",token=admin)['products']==[],"Admin page overflow")
    print("Catalog HTTP passed: public browse/details, sort caches, independent admin permission, version conflict, async synchronization")
if __name__=="__main__":main()
