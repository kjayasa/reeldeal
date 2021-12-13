const data = require( `../content/data/data.json`);

export default (key)=>{
    const msg=`${key} - ${JSON.stringify(data)}`
    return data[key]??msg;

} 